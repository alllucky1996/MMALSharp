﻿// <copyright file="MMALVideoFileDecoder.cs" company="Techyian">
// Copyright (c) Ian Auty. All rights reserved.
// Licensed under the MIT License. Please see LICENSE.txt for License info.
// </copyright>

using System;
using System.Text;
using System.Threading.Tasks;
using MMALSharp.Callbacks.Providers;
using MMALSharp.Common.Utility;
using MMALSharp.Handlers;
using MMALSharp.Native;
using MMALSharp.Ports;
using MMALSharp.Ports.Inputs;
using MMALSharp.Ports.Outputs;

namespace MMALSharp.Components
{
    /// <summary>
    /// This component is used to decode video data stored in a stream.
    /// </summary>
    public class MMALVideoFileDecoder : MMALVideoDecoder, IMMALConvert
    {
        /// <summary>
        /// Creates a new instance of <see cref="MMALImageFileDecoder"/>.
        /// </summary>
        /// <param name="handler">The capture handle to use.</param>
        public MMALVideoFileDecoder(ICaptureHandler handler)
            : base(handler)
        {
            this.Inputs[0].Handler = handler;
        }

        /// <summary>
        /// The working queue of buffer headers.
        /// </summary>
        public static MMALQueueImpl WorkingQueue { get; set; }

        /// <inheritdoc />>
        public override unsafe MMALDownstreamComponent ConfigureInputPort(MMALEncoding encodingType, MMALEncoding pixelFormat, int width, int height, bool zeroCopy = false)
        {
            this.InitialiseInputPort(0);

            if (encodingType != null)
            {
                this.Inputs[0].Ptr->Format->Encoding = encodingType.EncodingVal;
            }

            this.Inputs[0].Ptr->Format->Type = MMALFormat.MMAL_ES_TYPE_T.MMAL_ES_TYPE_VIDEO;
            this.Inputs[0].Ptr->Format->Es->Video.Height = width;
            this.Inputs[0].Ptr->Format->Es->Video.Width = height;
            this.Inputs[0].Ptr->Format->Es->Video.FrameRate = new MMAL_RATIONAL_T(25, 1);
            this.Inputs[0].Ptr->Format->Es->Video.Par = new MMAL_RATIONAL_T(1, 1);

            this.Inputs[0].EncodingType = encodingType;

            this.Inputs[0].Commit();

            this.Inputs[0].Ptr->BufferNum = Math.Max(this.Inputs[0].Ptr->BufferNumRecommended, this.Inputs[0].Ptr->BufferNumMin);
            this.Inputs[0].Ptr->BufferSize = Math.Max(this.Inputs[0].Ptr->BufferSizeRecommended, this.Inputs[0].Ptr->BufferSizeMin);

            return this;
        }

        /// <inheritdoc />>
        public override unsafe MMALDownstreamComponent ConfigureOutputPort(int outputPort, MMALEncoding encodingType, MMALEncoding pixelFormat, int quality, int bitrate = 0, bool zeroCopy = false)
        {
            this.InitialiseOutputPort(outputPort);

            if (this.ProcessingPorts.ContainsKey(outputPort))
            {
                this.ProcessingPorts.Remove(outputPort);
            }

            this.ProcessingPorts.Add(outputPort, this.Outputs[outputPort]);

            if (encodingType != null)
            {
                this.Outputs[outputPort].Ptr->Format->Encoding = encodingType.EncodingVal;
            }

            if (zeroCopy)
            {
                this.Outputs[outputPort].ZeroCopy = true;
                this.Outputs[outputPort].SetParameter(MMALParametersCommon.MMAL_PARAMETER_ZERO_COPY, true);
            }

            this.Outputs[outputPort].Commit();

            this.Outputs[outputPort].EncodingType = encodingType;

            this.Outputs[outputPort].Ptr->BufferNum = Math.Max(this.Outputs[outputPort].Ptr->BufferNumRecommended, this.Outputs[outputPort].Ptr->BufferNumMin);
            this.Outputs[outputPort].Ptr->BufferSize = Math.Max(this.Outputs[outputPort].Ptr->BufferSizeRecommended, this.Outputs[outputPort].Ptr->BufferSizeMin);
            this.Outputs[outputPort].ManagedOutputCallback = OutputCallbackProvider.FindCallback(this.Outputs[outputPort]);

            return this;
        }

        /// <summary>
        /// Encodes/decodes user provided image data.
        /// </summary>
        /// <param name="outputPort">The output port to begin processing on. Usually will be 0.</param>
        /// <returns>An awaitable task.</returns>
        public virtual async Task Convert(int outputPort = 0)
        {
            MMALLog.Logger.Info("Beginning Video decode from stream. Please note, this process may take some time depending on the size of the input stream.");

            // Enable control, input and output ports. Input & Output ports should have been pre-configured by user prior to this point.
            this.Control.Start();
            this.Inputs[0].Start();
            this.Outputs[outputPort].Start();

            this.EnableComponent();

            WorkingQueue = MMALQueueImpl.Create();

            var eosReceived = false;

            while (!eosReceived)
            {
                await this.WaitForTriggers().ConfigureAwait(false);

                this.GetAndSendInputBuffer();

                MMALLog.Logger.Debug("Getting processed output pool buffer");
                while (true)
                {
                    MMALBufferImpl buffer;
                    lock (OutputPort.OutputLock)
                    {
                        buffer = WorkingQueue.GetBuffer();
                    }

                    if (buffer.CheckState())
                    {
                        eosReceived = ((int)buffer.Flags & (int)MMALBufferProperties.MMAL_BUFFER_HEADER_FLAG_EOS) == (int)MMALBufferProperties.MMAL_BUFFER_HEADER_FLAG_EOS;

                        if (buffer.Cmd > 0)
                        {
                            if (buffer.Cmd == MMALEvents.MMAL_EVENT_FORMAT_CHANGED)
                            {
                                this.ProcessFormatChangedEvent(buffer);
                            }
                            else
                            {
                                lock (OutputPort.OutputLock)
                                {
                                    buffer.Release();
                                }
                            }

                            continue;
                        }
                        else
                        {
                            if (buffer.Length > 0)
                            {
                                this.Outputs[0].ManagedOutputCallback.Callback(buffer);
                            }
                            else
                            {
                                MMALLog.Logger.Debug("Buffer length empty.");
                            }

                            // Ensure we release the buffer before any signalling or we will cause a memory leak due to there still being a reference count on the buffer.                    
                            lock (OutputPort.OutputLock)
                            {
                                buffer.Release();
                            }
                        }
                    }
                    else
                    {
                        MMALLog.Logger.Debug("Buffer null.");
                        break;
                    }
                }

                this.GetAndSendOutputBuffer();
            }

            MMALLog.Logger.Info("Received EOS. Exiting.");

            this.DisableComponent();
            this.CleanPortPools();
            WorkingQueue.Dispose();
        }

        internal override void InitialiseInputPort(int inputPort)
        {
            this.Inputs[inputPort] = new VideoFileDecodeInputPort(this.Inputs[inputPort]);
        }

        internal override void InitialiseOutputPort(int outputPort)
        {
            this.Outputs[outputPort] = new VideoFileDecodeOutputPort(this.Outputs[outputPort]);
        }

        private unsafe void ConfigureOutputPortWithoutInit(int outputPort, MMALEncoding encodingType)
        {
            if (encodingType != null)
            {
                this.Outputs[outputPort].Ptr->Format->Encoding = encodingType.EncodingVal;
            }

            this.Outputs[outputPort].EncodingType = encodingType;

            this.Outputs[outputPort].Ptr->BufferNum = Math.Max(this.Outputs[outputPort].Ptr->BufferNumRecommended, this.Outputs[outputPort].Ptr->BufferNumMin);
            this.Outputs[outputPort].Ptr->BufferSize = Math.Max(this.Outputs[outputPort].Ptr->BufferSizeRecommended, this.Outputs[outputPort].Ptr->BufferSizeMin);

            MMALLog.Logger.Info($"New buffer number {this.Outputs[outputPort].Ptr->BufferNum}");
            MMALLog.Logger.Info($"New buffer size {this.Outputs[outputPort].Ptr->BufferSize}");

            this.Outputs[outputPort].Commit();
        }

        private void LogFormat(MMALEventFormat format, PortBase port)
        {
            StringBuilder sb = new StringBuilder();

            if (port != null)
            {
                switch (port.PortType)
                {
                    case PortType.Input:
                        sb.AppendLine("Port Type: Input");
                        break;
                    case PortType.Output:
                        sb.AppendLine("Port Type: Output");
                        break;
                    case PortType.Control:
                        sb.AppendLine("Port Type: Control");
                        break;
                    default:
                        break;
                }
            }

            sb.AppendLine($"FourCC: {format.FourCC}");
            sb.AppendLine($"Width: {format.Width}");
            sb.AppendLine($"Height: {format.Height}");
            sb.AppendLine($"Crop: {format.CropX}, {format.CropY}, {format.CropWidth}, {format.CropHeight}");
            sb.AppendLine($"Pixel aspect ratio: {format.ParNum}, {format.ParDen}. Frame rate: {format.FramerateNum}, {format.FramerateDen}");

            if (port != null)
            {
                sb.AppendLine($"Port info: Buffers num: {port.BufferNum}(opt {port.BufferNumRecommended}, min {port.BufferNumMin}). Size: {port.BufferSize} (opt {port.BufferSizeRecommended}, min {port.BufferSizeMin}). Alignment: {port.BufferAlignmentMin}");
            }

            MMALLog.Logger.Info(sb.ToString());
        }

        private void GetAndSendInputBuffer()
        {
            // Get buffer from input port pool                
            MMALBufferImpl inputBuffer;
            lock (InputPort.InputLock)
            {
                inputBuffer = this.Inputs[0].BufferPool.Queue.GetBuffer();

                if (inputBuffer.CheckState())
                {
                    // Populate the new input buffer with user provided image data.
                    var result = this.Inputs[0].ManagedInputCallback.Callback(inputBuffer);
                    inputBuffer.ReadIntoBuffer(result.BufferFeed, result.DataLength, result.EOF);

                    this.Inputs[0].SendBuffer(inputBuffer);
                }
            }
        }

        private void GetAndSendOutputBuffer(int outputPort = 0)
        {
            while (true)
            {
                lock (OutputPort.OutputLock)
                {
                    var tempBuf2 = this.Outputs[outputPort].BufferPool.Queue.GetBuffer();

                    if (tempBuf2.CheckState())
                    {
                        // Send empty buffers to the output port of the decoder                                          
                        this.Outputs[outputPort].SendBuffer(tempBuf2);
                    }
                    else
                    {
                        MMALLog.Logger.Debug("GetAndSendOutputBuffer: Buffer null.");
                        break;
                    }
                }
            }
        }

        private void ProcessFormatChangedEvent(MMALBufferImpl buffer, int outputPort = 0)
        {
            MMALLog.Logger.Debug("Received MMAL_EVENT_FORMAT_CHANGED event");

            var ev = MMALEventFormat.GetEventFormat(buffer);

            MMALLog.Logger.Debug("-- Event format changed from -- ");
            this.LogFormat(new MMALEventFormat(this.Outputs[outputPort].Format), this.Outputs[outputPort]);

            MMALLog.Logger.Debug("-- To -- ");
            this.LogFormat(ev, null);

            // Port format changed
            this.Outputs[outputPort].ManagedOutputCallback.Callback(buffer);

            lock (OutputPort.OutputLock)
            {
                buffer.Release();
            }

            this.Outputs[outputPort].DisablePort();

            while (this.Outputs[outputPort].BufferPool.Queue.QueueLength() < this.Outputs[outputPort].BufferPool.HeadersNum)
            {
                MMALLog.Logger.Debug("Queue length less than buffer pool num");
                lock (OutputPort.OutputLock)
                {
                    MMALLog.Logger.Debug("Getting buffer via Queue.Wait");
                    var tempBuf = WorkingQueue.Wait();
                    tempBuf.Release();
                }
            }

            this.Outputs[outputPort].BufferPool.Dispose();
            this.Outputs[outputPort].BufferPool = null;

            this.Outputs[outputPort].FullCopy(ev);

            this.ConfigureOutputPortWithoutInit(0, this.Outputs[outputPort].EncodingType);

            this.Outputs[outputPort].EnableOutputPort(false);
        }

        private async Task WaitForTriggers(int outputPort = 0)
        {
            MMALLog.Logger.Debug("Waiting for trigger signal");

            // Wait until the process is complete.
            while (!this.Inputs[0].Trigger)
            {
                await Task.Delay(2000).ConfigureAwait(false);
                break;
            }

            MMALLog.Logger.Debug("Resetting trigger state.");
            this.Inputs[0].Trigger = false;
            this.Outputs[outputPort].Trigger = false;
        }
    }
}