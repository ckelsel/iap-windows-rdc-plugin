﻿//
// Copyright 2019 Google LLC
//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using Google.Solutions.Compute.Iap;
using Google.Solutions.Compute.Net;
using NUnit.Framework;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Google.Solutions.Compute.Test.Iap
{
    

    [TestFixture]
    public class TestSshRelayStreamReading : FixtureBase
    {
        private readonly CancellationTokenSource tokenSource = new CancellationTokenSource();
        
        [Test]
        public async Task ConnectionOpenedByFirstRead()
        {
            var stream = new MockStream()
            {
                ExpectedReadData = new byte[][]
                {
                    new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                    new byte[]{ }
                }
            };
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = stream
            };
            var relay = new SshRelayStream(endpoint);

            Assert.AreEqual(0, endpoint.ConnectCount);

            byte[] buffer = new byte[relay.MinReadSize];
            await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);

            Assert.AreEqual(1, endpoint.ConnectCount);
        }

        [Test]
        public void TinyBufferCausesIndexOutOfRangeException()
        {
            var stream = new MockStream()
            {
                ExpectedReadData = new byte[][]
                {
                    new byte[64],
                    new byte[]{ }
                }
            };
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = stream
            };
            var relay = new SshRelayStream(endpoint);

            Assert.AreEqual(0, endpoint.ConnectCount);

            byte[] buffer = new byte[relay.MinReadSize - 1];

            AssertEx.ThrowsAggregateException<IndexOutOfRangeException>(() =>
            {
                relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token).Wait();
            });
        }

        [Test]
        public void TruncatedMessageCausesException()
        {
            var stream = new MockStream()
            {
                ExpectedReadData = new byte[][]
                {
                    new byte[]{ 0 },
                    new byte[]{ }
                }
            };
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = stream
            };
            var relay = new SshRelayStream(endpoint);

            Assert.AreEqual(0, endpoint.ConnectCount);

            byte[] buffer = new byte[relay.MinReadSize];

            AssertEx.ThrowsAggregateException<InvalidServerResponseException>(() =>
            {
                relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token).Wait();
            });
        }

        [Test]
        public void UnrecognizedMessageTagAtStartCausesException(
            [Values(
                (byte)MessageTag.UNUSED,
                (byte)MessageTag.DEPRECATED,
                (byte)MessageTag.ACK_LATENCY,
                (byte)MessageTag.REPLY_LATENCY,
                (byte)MessageTag.ACK + 1)] byte tag)
        {
            var stream = new MockStream()
            {
                ExpectedReadData = new byte[][]
                {
                    new byte[]{ 0, tag },
                    new byte[]{ }
                }
            };
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = stream
            };
            var relay = new SshRelayStream(endpoint);

            Assert.AreEqual(0, endpoint.ConnectCount);

            AssertEx.ThrowsAggregateException<InvalidServerResponseException>(() =>
            {
                byte[] buffer = new byte[relay.MinReadSize];
                relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token).Wait();
            });
        }

        [Test]
        public async Task UnrecognizedMessageTagAfterStartCausesException(
            [Values(
                (byte)MessageTag.UNUSED,
                (byte)MessageTag.DEPRECATED,
                (byte)MessageTag.ACK_LATENCY,
                (byte)MessageTag.REPLY_LATENCY,
                (byte)MessageTag.ACK + 1)] byte tag)
        {
            var stream = new MockStream()
            {
                ExpectedReadData = new byte[][]
                {
                    new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                    new byte[]{ 0, tag },
                    new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 2, 0xA, 0xB },
                    new byte[]{ 0, tag },
                    new byte[]{ }
                }
            };
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = stream
            };
            var relay = new SshRelayStream(endpoint);

            byte[] buffer = new byte[relay.MinReadSize];
            int bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(2, bytesRead);
                    Assert.AreEqual(0xA, buffer[0]);
                    Assert.AreEqual(0xB, buffer[1]);
        }

        [Test]
        public async Task AckTrimsUnacknoledgedQueue()
        {
            byte[] request = new byte[] { 1, 2, 3, 4 };

            var stream = new MockStream()
            {
                ExpectedReadData = new byte[][]
                {
                    new byte[]{ 0, (byte)MessageTag.ACK, 0, 0, 0, 0, 0, 0, 0, (byte)request.Length },
                    new byte[]{ 0, (byte)MessageTag.ACK, 0, 0, 0, 0, 0, 0, 0, (byte)(request.Length*3) },
                    new byte[]{ }
                }
            };
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = stream
            };
            var relay = new SshRelayStream(endpoint);

            Assert.AreEqual(0, relay.UnacknoledgedMessageCount);
            Assert.AreEqual(0, relay.ExpectedAck);

            // Send 3 messages.
            await relay.WriteAsync(request, 0, request.Length, tokenSource.Token);
            await relay.WriteAsync(request, 0, request.Length, tokenSource.Token);
            await relay.WriteAsync(request, 0, request.Length, tokenSource.Token);

            Assert.AreEqual(3, relay.UnacknoledgedMessageCount);
            Assert.AreEqual((byte)(request.Length * 3), relay.ExpectedAck);

            // Receive 2 ACKs.
            byte[] buffer = new byte[relay.MinReadSize];
            int bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);

            Assert.AreEqual(0, bytesRead);
            Assert.AreEqual(0, relay.UnacknoledgedMessageCount);
            Assert.AreEqual(0, relay.ExpectedAck);
        }

        [Test]
        public async Task ZeroAckCausesException()
        {
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = new MockStream()
                {
                    ExpectedReadData = new byte[][]
                    {
                        new byte[]{ 0, (byte)MessageTag.ACK, 0, 0, 0, 0 },
                        new byte[]{ }
                    }
                }
            };
            var relay = new SshRelayStream(endpoint);

            // Send a message.
            byte[] request = new byte[] { 1, 2, 3, 4 };
            await relay.WriteAsync(request, 0, request.Length, tokenSource.Token);

            // Receive invalid ACK.
            AssertEx.ThrowsAggregateException<InvalidServerResponseException>(() =>
            {
                byte[] buffer = new byte[relay.MinReadSize];
                int bytesRead = relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token).Result;
            });
        }

        [Test]
        public async Task MismatchedAckCausesException()
        {
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = new MockStream()
                {
                    ExpectedReadData = new byte[][]
                    {
                        new byte[]{ 0, (byte)MessageTag.ACK, 0, 0, 0, 10 },
                        new byte[]{ }
                    }
                }
            };
            var relay = new SshRelayStream(endpoint);

            // Send 5 bytes.
            byte[] request = new byte[] { 1, 2, 3, 4 };
            await relay.WriteAsync(request, 0, request.Length, tokenSource.Token);

            // Receive invalid ACK for byte 10.
            AssertEx.ThrowsAggregateException<InvalidServerResponseException>(() =>
            {
                byte[] buffer = new byte[relay.MinReadSize];
                int bytesRead = relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token).Result;
            });
        }

        [Test]
        public async Task DataHeaderIsTrimmed()
        {
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = new MockStream()
                {
                    ExpectedReadData = new byte[][]
                    {
                        new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                        new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 2, 0xA, 0xB },
                        new byte[]{ }
                    }
                }
            };
            var relay = new SshRelayStream(endpoint);

            byte[] buffer = new byte[relay.MinReadSize];
            int bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(2, bytesRead);
            Assert.AreEqual(0xA, buffer[0]);
            Assert.AreEqual(0xB, buffer[1]);
        }

        [Test]
        public async Task ReadAfterGracefulServerCloseReturnsZero()
        {
            var stream = new MockStream()
            {
                ExpectedReadData = new byte[][]
                {
                    new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                    new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 1, 1 },
                    new byte[]{ }
                }
            };
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = stream
            };
            var relay = new SshRelayStream(endpoint);

            byte[] buffer = new byte[relay.MinReadSize];
            int bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(1, bytesRead);

            bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(0, bytesRead);
        }

        [Test]
        public async Task ReadAfterDestinationReadFailedReturnsZero()
        {
            var stream = new MockStream()
            {
                ExpectedReadData = new byte[][]
                {
                    new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                    new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 1, 1 }
                },
                ExpectServerCloseCodeOnRead = (WebSocketCloseStatus)CloseCode.DESTINATION_READ_FAILED
            };
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStream = stream
            };
            var relay = new SshRelayStream(endpoint);

            byte[] buffer = new byte[relay.MinReadSize];
            int bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(1, bytesRead);

            bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(0, bytesRead);
        }

        [Test]
        public async Task ReadAfterForcefulServerCloseCausesAnotherConnectIfNoDataReadBefore(
            [Values(
                WebSocketCloseStatus.EndpointUnavailable,
                WebSocketCloseStatus.InvalidMessageType,
                WebSocketCloseStatus.ProtocolError,
                (WebSocketCloseStatus)CloseCode.BAD_ACK,
                (WebSocketCloseStatus)CloseCode.ERROR_UNKNOWN,
                (WebSocketCloseStatus)CloseCode.INVALID_TAG,
                (WebSocketCloseStatus)CloseCode.INVALID_WEBSOCKET_OPCODE,
                (WebSocketCloseStatus)CloseCode.REAUTHENTICATION_REQUIRED
            )] WebSocketCloseStatus closeStatus)
        {
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStreams = new[] {
                    new MockStream()
                    {
                        ExpectServerCloseCodeOnRead = closeStatus
                    },
                    new MockStream()
                    {
                        ExpectedReadData = new byte[][]
                        {
                            new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                            new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 2, 1, 2 }
                        }
                    }
                }
            };
            var relay = new SshRelayStream(endpoint);

            // connection breaks, triggering another connect.
            var buffer = new byte[relay.MinReadSize];
            int bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(2, bytesRead);
            Assert.AreEqual(2, endpoint.ConnectCount);
            Assert.AreEqual(0, endpoint.ReconnectCount);
        }


        [Test]
        public async Task ReadFailedReconnectCausesException(
            [Values(
                (WebSocketCloseStatus)CloseCode.SID_UNKNOWN,
                (WebSocketCloseStatus)CloseCode.SID_IN_USE
            )] WebSocketCloseStatus closeStatus)
        {
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStreams = new[] {
                    new MockStream()
                    {
                        ExpectedReadData = new byte[][]
                        {
                            new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                            new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 1, 1 }
                        },
                        ExpectServerCloseCodeOnRead = WebSocketCloseStatus.ProtocolError
                    },
                    new MockStream()
                    {
                        ExpectServerCloseCodeOnRead = closeStatus
                    }
                }
            };
            var relay = new SshRelayStream(endpoint);

            // read data
            var buffer = new byte[relay.MinReadSize];
            int bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(1, bytesRead);

            // connection breaks, triggering a reconnect that will fail.
            AssertEx.ThrowsAggregateException<WebSocketStreamClosedByServerException>(() =>
            {
                relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token).Wait();
            });
        }

        [Test]
        public async Task ReadAfterForcefulServerCloseCausesReconnectIfDataReadBefore(
            [Values(
                WebSocketCloseStatus.EndpointUnavailable,
                WebSocketCloseStatus.InvalidMessageType,
                WebSocketCloseStatus.ProtocolError,
                (WebSocketCloseStatus)CloseCode.BAD_ACK,
                (WebSocketCloseStatus)CloseCode.ERROR_UNKNOWN,
                (WebSocketCloseStatus)CloseCode.INVALID_TAG,
                (WebSocketCloseStatus)CloseCode.INVALID_WEBSOCKET_OPCODE,
                (WebSocketCloseStatus)CloseCode.REAUTHENTICATION_REQUIRED
            )] WebSocketCloseStatus closeStatus)
        {
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStreams = new[] {
                    new MockStream()
                    {
                        ExpectedReadData = new byte[][]
                        {
                            new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                            new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 1, 1 }
                        },
                        ExpectServerCloseCodeOnRead = closeStatus
                    },
                    new MockStream()
                    {
                        ExpectedReadData = new byte[][]
                        {
                            new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                            new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 2, 1, 2 }
                        }
                    }
                }
            };
            var relay = new SshRelayStream(endpoint);

            // read data
            var buffer = new byte[relay.MinReadSize];
            int bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(1, bytesRead);

            // connection breaks, triggering a reconnect.
            bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(2, bytesRead);
            Assert.AreEqual(1, endpoint.ConnectCount);
            Assert.AreEqual(1, endpoint.ReconnectCount);
        }


        [Test]
        public async Task ReadAfterWriteAndForcefulServerCloseCausesReconnect(
            [Values(
                WebSocketCloseStatus.EndpointUnavailable,
                WebSocketCloseStatus.InvalidMessageType,
                WebSocketCloseStatus.ProtocolError,
                (WebSocketCloseStatus)CloseCode.BAD_ACK,
                (WebSocketCloseStatus)CloseCode.ERROR_UNKNOWN,
                (WebSocketCloseStatus)CloseCode.INVALID_TAG,
                (WebSocketCloseStatus)CloseCode.INVALID_WEBSOCKET_OPCODE,
                (WebSocketCloseStatus)CloseCode.REAUTHENTICATION_REQUIRED
            )] WebSocketCloseStatus closeStatus)
        {
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStreams = new[] {
                    new MockStream()
                    {
                        ExpectedReadData = new byte[][]
                        {
                            new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                            new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 1, 1 }
                        },
                        ExpectServerCloseCodeOnRead = closeStatus
                    },
                    new MockStream()
                    {
                        ExpectedReadData = new byte[][]
                        {
                            new byte[]{ 0, (byte)MessageTag.RECONNECT_SUCCESS_ACK, 0, 0, 0, 0 },
                            new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 1, 1 }
                        },
                        ExpectServerCloseCodeOnRead = closeStatus
                    }
                }
            };
            var relay = new SshRelayStream(endpoint);

            // Write something so that a connection breakdown causes a reconnect,
            // not just another connect.
            var request = new byte[] { 1, 2, 3 };
            await relay.WriteAsync(request, 0, request.Length, tokenSource.Token);

            byte[] buffer = new byte[relay.MinReadSize];
            int bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(1, bytesRead);
            Assert.AreEqual(1, endpoint.ConnectCount);
            Assert.AreEqual(0, endpoint.ReconnectCount);

            // connection breaks, triggering reconnect.

            bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(1, bytesRead);
            Assert.AreEqual(1, endpoint.ConnectCount);
            Assert.AreEqual(1, endpoint.ReconnectCount);
        }

        [Test]
        public async Task ReadAfterClientCloseCausesException()
        {
            var endpoint = new MockSshRelayEndpoint()
            {
                ExpectedStreams = new[] {
                    new MockStream()
                    {
                        ExpectedReadData = new byte[][]
                        {
                            new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                            new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 1, 1 }
                        }
                    },
                    new MockStream()
                    {
                        ExpectedReadData = new byte[][]
                        {
                            new byte[]{ 0, (byte)MessageTag.CONNECT_SUCCESS_SID, 0, 0, 0, 1, 0 },
                            new byte[]{ 0, (byte)MessageTag.DATA, 0, 0, 0, 2, 1, 2 }
                        }
                    }
                }
            };
            var relay = new SshRelayStream(endpoint);

            // Write and read something.
            byte[] request = new byte[] { 1, 2, 3, 4 };
            await relay.WriteAsync(request, 0, request.Length, tokenSource.Token);

            byte[] buffer = new byte[relay.MinReadSize];
            int bytesRead = await relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token);
            Assert.AreEqual(1, bytesRead);

            Assert.AreEqual(1, endpoint.ConnectCount);
            Assert.AreEqual(0, endpoint.ReconnectCount);

            await relay.CloseAsync(tokenSource.Token);

            AssertEx.ThrowsAggregateException<NetworkStreamClosedException>(() =>
            {
                bytesRead = relay.ReadAsync(buffer, 0, buffer.Length, tokenSource.Token).Result;
            });
        }
    }
}
