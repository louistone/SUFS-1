﻿using ClientProto;
using Grpc.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataNode
{
    class ClientHandler : ClientProto.ClientProto.ClientProtoBase
    {
        public override Task<ClientProto.StatusResponse> GetReady(ClientProto.BlockInfo blockInfo, ServerCallContext context)
        {
            ClientProto.StatusResponse response = new ClientProto.StatusResponse { Type = ClientProto.StatusResponse.Types.StatusType.Success };
            if (blockInfo.IpAddress.Count() == 0)
            {
                response.Type = ClientProto.StatusResponse.Types.StatusType.Ready;
                return Task.FromResult(response);
            }
            else
            {
                Guid blockId = Guid.Parse(blockInfo.BlockId.Value);
                Channel channel = null;
                string ipAddress = "";
                try
                {
                    ipAddress = blockInfo.IpAddress[0];
                    blockInfo.IpAddress.RemoveAt(0);
                    channel = ConnectionManager.Instance.CreateChannel(blockId, ipAddress, Constants.Port.ToString());
                    var client = new ClientProto.ClientProto.ClientProtoClient(channel);

                    client.GetReady(blockInfo);
                }
                catch (RpcException e)
                {
                    ConnectionManager.Instance.ShutDownChannel(blockId, channel);
                    response.Message = "Failed to get ready for " + ipAddress + ": " + e.Message;
                }

                // We will always return ready as if it reaches this far there is at least 1 node in the pipeline, which is good enough!
                return Task.FromResult(response);
            }
        }

        /// <summary>
        /// Server side handler of the WriteDataBlock RPC. DataNode writes block to a file and then returns success status of write.
        /// </summary>
        /// <param name="requestStream">Stream of bytes</param>
        /// <param name="context">Context of call. Contains metadata containing blockid and list of addresses</param>
        /// <returns>Status of task</returns>
        public override async Task<ClientProto.StatusResponse> WriteBlock(Grpc.Core.IAsyncStreamReader<ClientProto.BlockData> requestStream, ServerCallContext context)
        {
            List<Metadata.Entry> metaData = context.RequestHeaders.ToList();

            // Get blockID
            Guid blockId = Util.GetBlockID(metaData);
            int blockSize = Util.GetBlockSize(metaData);

            string filePath = BlockStorage.Instance.CreateFile(blockId);

            Channel channel = ConnectionManager.Instance.GetChannel(blockId);

            ClientProto.StatusResponse response = new ClientProto.StatusResponse { Type = ClientProto.StatusResponse.Types.StatusType.Success };
            // No channel found means last datanode in pipe
            if (channel != null)
            {
                response = await WriteAndForwardBlock(requestStream, context, channel, filePath, blockId, blockSize);
            }
            else // Just write to file
            {
                response = await WriteBlock(requestStream, filePath, blockId, blockSize);
            }

            if(response.Type == ClientProto.StatusResponse.Types.StatusType.Success)
            {
                Console.WriteLine("Done writing block: " + blockId.ToString());
                // Send block report to NameNode
                var client = new DataNodeProto.DataNodeProto.DataNodeProtoClient(ConnectionManager.Instance.NameNodeConnection);
                BlockReport.SendSingleBlockReport(client);
            }

            return response;
        }

        /// <summary>
        /// Writes block to disk and forwards to next DataNode
        /// </summary>
        /// <param name="requestStream">Stream of bytes</param>
        /// <param name="context">Context of call</param>
        /// <param name="channel">Channel for pipe</param>
        /// <param name="filePath">Full path of file</param>
        /// <param name="blockId">Unique identifier of block</param>
        /// <returns>Status of writing the block</returns>
        public async Task<ClientProto.StatusResponse> WriteAndForwardBlock(Grpc.Core.IAsyncStreamReader<ClientProto.BlockData> requestStream, ServerCallContext context, Channel channel, string filePath, Guid blockId, int blockSize)
        {
            bool success = true;
            string message = "";
            var client = new ClientProto.ClientProto.ClientProtoClient(channel);
            using (var call = client.WriteBlock(context.RequestHeaders))
            {
                bool dataNodeFailed = false;

                using (var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough)) // 128KB
                {
                    while (await requestStream.MoveNext())
                    {
                        try
                        {
                            var blockData = requestStream.Current;
                            byte[] data = blockData.Data.ToByteArray();

                            // Write to file
                            stream.Write(data, 0, data.Length);

                            // Don't need to forward data in pipe if datanode failed
                            if (!dataNodeFailed)
                            {
                                try
                                {
                                    // Send data through pipe
                                    await call.RequestStream.WriteAsync(blockData);
                                }
                                catch(RpcException e)
                                {
                                    dataNodeFailed = true;
                                    Console.WriteLine("Writing block failed: " + e.Message);
                                    message = e.Message;
                                }
                            }
                        }
                        catch (IOException e)
                        {
                            dataNodeFailed = true;
                            message = e.Message;
                            success = false;
                        }
                    }
                    stream.Flush();
                }

                ClientProto.StatusResponse resp = new ClientProto.StatusResponse { Type = ClientProto.StatusResponse.Types.StatusType.Fail, Message = message };

                // If DataNode did not fail, get response and shut down
                if (!dataNodeFailed)
                {
                    await call.RequestStream.CompleteAsync();

                    resp = await call.ResponseAsync;
                    ConnectionManager.Instance.ShutDownChannel(blockId, channel);
                }

                // If write was successful and block size is correct, return success
                // Otherwise return the response sent down through pipe
                return (success && BlockStorage.Instance.ValidateBlock(blockId, filePath, blockSize)) ? new ClientProto.StatusResponse { Type = ClientProto.StatusResponse.Types.StatusType.Success } : resp;
            }
        }

        /// <summary>
        /// Writes block to disk
        /// </summary>
        /// <param name="requestStream">Stream of bytes</param>
        /// <param name="filePath">Full path of file</param>
        /// <param name="blockId">Unique identifier of block</param>
        /// <returns>Status of writing the block</returns>
        public async Task<ClientProto.StatusResponse> WriteBlock(Grpc.Core.IAsyncStreamReader<ClientProto.BlockData> requestStream, string filePath, Guid blockId, int blockSize)
        {
            using (var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough)) // 128KB
            {
                while (await requestStream.MoveNext())
                {
                    try
                    {
                        var blockData = requestStream.Current;
                        byte[] data = blockData.Data.ToByteArray();

                        // Write to file
                        stream.Write(data, 0, data.Length);
                    }
                    catch (IOException e)
                    {
                        BlockStorage.Instance.DeleteBlock(blockId);
                        return new ClientProto.StatusResponse { Type = ClientProto.StatusResponse.Types.StatusType.Fail, Message = e.Message };
                    }
                }

                stream.Flush();
            }

            // If write was successful, make sure block size is correct
            return !BlockStorage.Instance.ValidateBlock(blockId, filePath, blockSize) ? new ClientProto.StatusResponse { Type = ClientProto.StatusResponse.Types.StatusType.Fail, Message = "Block size is not correct" } : new ClientProto.StatusResponse { Type = ClientProto.StatusResponse.Types.StatusType.Success };
        }
        
        /// <summary>
        /// Read block from disk
        /// </summary>
        /// <param name="id">UUID</param>
        /// <returns>BlockData</returns>        
        public override async Task ReadBlock(UUID id, IServerStreamWriter<BlockData> responseStream,ServerCallContext context)
        {
            byte[] blockData = BlockStorage.Instance.ReadBlock(Guid.Parse(id.Value));

            if (blockData != null)
            {
                int remaining = blockData.Length;

                while (remaining > 0)
                {
                    var copyLength = Math.Min(Constants.StreamChunkSize, remaining);
                    byte[] streamBuffer = new byte[copyLength];

                    Buffer.BlockCopy(
                        blockData,
                        blockData.Length - remaining,
                        streamBuffer, 0,
                        copyLength);

                    await responseStream.WriteAsync(new BlockData { Data = Google.Protobuf.ByteString.CopyFrom(streamBuffer) });

                    remaining -= copyLength;
                }
            }
        }
    }
}
