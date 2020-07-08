﻿/*
 * Copyright 2014 - 2017 Adaptive Financial Consulting Ltd
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0S
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Text;
using Adaptive.Aeron.Exceptions;
using Adaptive.Agrona;

namespace Adaptive.Aeron.Command
{
    /// <summary>
    /// Control message for adding or removing a publication
    /// 
    /// <para>
    /// 0                   1                   2                   3
    /// 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// |                         Correlation ID                        |
    /// |                                                               |
    /// +---------------------------------------------------------------+
    /// |                          Stream ID                            |
    /// +---------------------------------------------------------------+
    /// |                        Channel Length                         |
    /// +---------------------------------------------------------------+
    /// |                           Channel   (ASCII)                  ...
    /// ...                                                             |
    /// +---------------------------------------------------------------+
    /// </para>
    /// </summary>
    public class PublicationMessageFlyweight : CorrelatedMessageFlyweight
    {
        private static readonly int STREAM_ID_FIELD_OFFSET = CORRELATION_ID_FIELD_OFFSET + BitUtil.SIZE_OF_LONG;
        private static readonly int CHANNEL_OFFSET = STREAM_ID_FIELD_OFFSET + BitUtil.SIZE_OF_INT;

        private static readonly int MINIMUM_LENGTH = CHANNEL_OFFSET + BitUtil.SIZE_OF_INT;
        
        private int _lengthOfChannel;

        /// <summary>
        /// Get the stream id field
        /// </summary>
        /// <returns> stream id field </returns>
        public int StreamId()
        {
            return buffer.GetInt(offset + STREAM_ID_FIELD_OFFSET);
        }

        /// <summary>
        /// Set the stream id field
        /// </summary>
        /// <param name="streamId"> field value </param>
        /// <returns> flyweight </returns>
        public PublicationMessageFlyweight StreamId(int streamId)
        {
            buffer.PutInt(offset + STREAM_ID_FIELD_OFFSET, streamId);

            return this;
        }

        /// <summary>
        /// Get the channel field in ASCII
        /// </summary>
        /// <returns> channel field </returns>
        public string Channel()
        {
            return buffer.GetStringAscii(offset + CHANNEL_OFFSET);
        }

        /// <summary>
        /// Append the channel value to a <seealso cref="StringBuilder"/>.
        /// </summary>
        /// <param name="stringBuilder"> to append channel to. </param>
        public void AppendChannel(StringBuilder stringBuilder)
        {
            buffer.GetStringAscii(offset + CHANNEL_OFFSET, stringBuilder);
        }
        
        /// <summary>
        /// Set the channel field in ASCII
        /// </summary>
        /// <param name="channel"> field value </param>
        /// <returns> flyweight </returns>
        public PublicationMessageFlyweight Channel(string channel)
        {
            _lengthOfChannel = buffer.PutStringAscii(offset + CHANNEL_OFFSET, channel);

            return this;
        }

        /// <summary>
        /// Get the length of the current message
        /// 
        /// NB: must be called after the data is written in order to be accurate.
        /// </summary>
        /// <returns> the length of the current message </returns>
        public int Length()
        {
            return CHANNEL_OFFSET + _lengthOfChannel;
        }
        
        /// <summary>
        /// Validate buffer length is long enough for message.
        /// </summary>
        /// <param name="msgTypeId"> type of message. </param>
        /// <param name="length"> of message in bytes to validate. </param>
        public new void ValidateLength(int msgTypeId, int length)
        {
            if (length < MINIMUM_LENGTH)
            {
                throw new ControlProtocolException(ErrorCode.MALFORMED_COMMAND, "command=" + msgTypeId + " too short: length=" + length);
            }

            if ((length - MINIMUM_LENGTH) < buffer.GetInt(offset + CHANNEL_OFFSET))
            {
                throw new ControlProtocolException(ErrorCode.MALFORMED_COMMAND, "command=" + msgTypeId + " too short for channel: length=" + length);
            }
        }
    }
}