﻿using System;
using System.Net.Http;
using Raven.Client.Http;
using Sparrow.Json;

namespace Raven.Client.Server.Commands
{
        public class WaitForRaftIndexCommand : RavenCommand
        {
            private readonly long _index;

            public WaitForRaftIndexCommand(long index)
            {
                _index = index;
            }

            public override HttpRequestMessage CreateRequest(ServerNode node, out string url)
            {
                url = $"{node.Url}/rachis/waitfor?index={_index}";
                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                };
                return request;
            }

            public override void SetResponse(BlittableJsonReaderObject response, bool fromCache)
            {
                if (response == null)
                    ThrowInvalidResponse();
            }

            public override bool IsReadRequest => true;
        }
}
