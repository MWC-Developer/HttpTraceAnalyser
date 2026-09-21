using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml;

namespace HttpTraceAnalyser.Model
{
    /// <summary>
    /// Decodes Exchange ActiveSync WBXML message bodies without requiring Fiddler.
    /// </summary>
    internal static class EasWbxmlDecoder
    {
        private const string WbxmlContentType = "application/vnd.ms-sync.wbxml";

        private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<byte, string>> Tags =
            new Dictionary<int, IReadOnlyDictionary<byte, string>>
            {
                [0] = Tokens("Sync:05,Responses:06,Add:07,Change:08,Delete:09,Fetch:0A,SyncKey:0B,ClientId:0C,ServerId:0D,Status:0E,Collection:0F,Class:10,CollectionId:12,GetChanges:13,MoreAvailable:14,WindowSize:15,Commands:16,Options:17,FilterType:18,Conflict:1B,Collections:1C,ApplicationData:1D,DeletesAsMoves:1E,Supported:20,SoftDelete:21,MIMESupport:22,MIMETruncation:23,Wait:24,Limit:25,Partial:26,ConversationMode:27,MaxItems:28,HeartbeatInterval:29"),
                [1] = Tokens("Anniversary:05,AssistantName:06,AssistantTelephoneNumber:07,Birthday:08,Body:09,BodySize:0A,BodyTruncated:0B,Business2PhoneNumber:0C,BusinessCity:0D,BusinessCountry:0E,BusinessPostalCode:0F,BusinessState:10,BusinessStreet:11,BusinessFaxNumber:12,BusinessPhoneNumber:13,CarPhoneNumber:14,Categories:15,Category:16,Children:17,Child:18,CompanyName:19,Department:1A,Email1Address:1B,Email2Address:1C,Email3Address:1D,FileAs:1E,FirstName:1F,Home2PhoneNumber:20,HomeCity:21,HomeCountry:22,HomePostalCode:23,HomeState:24,HomeStreet:25,HomeFaxNumber:26,HomePhoneNumber:27,JobTitle:28,LastName:29,MiddleName:2A,MobilePhoneNumber:2B,OfficeLocation:2C,OtherCity:2D,OtherCountry:2E,OtherPostalCode:2F,OtherState:30,OtherStreet:31,PagerNumber:32,RadioPhoneNumber:33,Spouse:34,Suffix:35,Title:36,Webpage:37,YomiCompanyName:38,YomiFirstName:39,YomiLastName:3A,Picture:3C,Alias:3D,WeightedRank:3E"),
                [2] = Tokens("Attachment:05,Attachments:06,AttName:07,AttSize:08,Att0Id:09,AttMethod:0A,AttRemoved:0B,Body:0C,BodySize:0D,BodyTruncated:0E,DateReceived:0F,DisplayName:10,DisplayTo:11,Importance:12,MessageClass:13,Subject:14,Read:15,To:16,Cc:17,From:18,ReplyTo:19,AllDayEvent:1A,Categories:1B,Category:1C,DTStamp:1D,EndTime:1E,InstanceType:1F,BusyStatus:20,Location:21,MeetingRequest:22,Organizer:23,RecurrenceId:24,Reminder:25,ResponseRequested:26,Recurrences:27,Recurrence:28,Type:29,Until:2A,Occurrences:2B,Interval:2C,DayOfWeek:2D,DayOfMonth:2E,WeekOfMonth:2F,MonthOfYear:30,StartTime:31,Sensitivity:32,TimeZone:33,GlobalObjId:34,ThreadTopic:35,MIMEData:36,MIMETruncated:37,MIMESize:38,InternetCPID:39,Flag:3A,Status:3B,ContentClass:3C,FlagType:3D,CompleteTime:3E,DisallowNewTimeProposal:3F"),
                [4] = Tokens("TimeZone:05,AllDayEvent:06,Attendees:07,Attendee:08,Email:09,Name:0A,Body:0B,BodyTruncated:0C,BusyStatus:0D,Categories:0E,Category:0F,Rtf:10,DtStamp:11,EndTime:12,Exception:13,Exceptions:14,Deleted:15,ExceptionStartTime:16,Location:17,MeetingStatus:18,OrganizerEmail:19,OrganizerName:1A,Recurrence:1B,Type:1C,Until:1D,Occurrences:1E,Interval:1F,DayOfWeek:20,DayOfMonth:21,WeekOfMonth:22,MonthOfYear:23,Reminder:24,Sensitivity:25,Subject:26,StartTime:27,UID:28,AttendeeStatus:29,AttendeeType:2A,DisallowNewTimeProposal:2B,ResponseRequested:2C,AppointmentReplyTime:2D,ResponseType:2E,CalendarType:2F,IsLeapMonth:30,FirstDayOfWeek:31,OnlineMeetingConfLink:32,OnlineMeetingExternalLink:33,ClientUid:34"),
                [5] = Tokens("MoveItems:05,Move:06,SrcMsgId:07,SrcFldId:08,DstFldId:09,Response:0A,Status:0B,DstMsgId:0C"),
                [6] = Tokens("GetItemEstimate:05,Version:06,Collections:07,Collection:08,Class:09,CollectionId:0A,DateTime:0B,Estimate:0C,Response:0D,Status:0E"),
                [7] = Tokens("Folders:05,Folder:06,DisplayName:07,ServerId:08,ParentId:09,Type:0A,Response:0B,Status:0C,ContentClass:0D,Changes:0E,Add:0F,Delete:10,Update:11,SyncKey:12,FolderCreate:13,FolderDelete:14,FolderUpdate:15,FolderSync:16,Count:17"),
                [8] = Tokens("CalendarId:05,CollectionId:06,MeetingResponse:07,RequestId:08,Request:09,Result:0A,Status:0B,UserResponse:0C,InstanceId:0E,ProposedStartTime:0F,ProposedEndTime:10,SendResponse:11"),
                [9] = Tokens("Body:05,BodySize:06,BodyTruncated:07,Categories:08,Category:09,Complete:0A,DateCompleted:0B,DueDate:0C,UTCDueDate:0D,Importance:0E,Recurrence:0F,Type:10,Start:11,Until:12,Occurrences:13,Interval:14,DayOfMonth:15,DayOfWeek:16,WeekOfMonth:17,MonthOfYear:18,Regenerate:19,DeadOccur:1A,ReminderSet:1B,ReminderTime:1C,Sensitivity:1D,StartDate:1E,UTCStartDate:1F,Subject:20,OrdinalDate:21,SubOrdinalDate:22,CalendarType:23,IsLeapMonth:24,FirstDayOfWeek:25"),
                [10] = Tokens("ResolveRecipients:05,Response:06,Status:07,Type:08,Recipient:09,DisplayName:0A,EmailAddress:0B,Certificates:0C,Certificate:0D,MiniCertificate:0E,Options:0F,To:10,CertificateRetrieval:11,RecipientCount:12,MaxCertificates:13,MaxAmbiguousRecipients:14,CertificateCount:15,Availability:16,StartTime:17,EndTime:18,MergedFreeBusy:19,Picture:1A,MaxSize:1B,Data:1C,MaxPictures:1D"),
                [13] = Tokens("Provision:05,Policies:06,Policy:07,PolicyType:08,PolicyKey:09,Data:0A,Status:0B,RemoteWipe:0C,EASProvisionDoc:0D,DevicePasswordEnabled:0E,AlphanumericDevicePasswordRequired:0F,PasswordRecoveryEnabled:10,RequireStorageCardEncryption:11,AttachmentsEnabled:12,MinDevicePasswordLength:13,MaxInactivityTimeDeviceLock:14,MaxDevicePasswordFailedAttempts:15,MaxAttachmentSize:16,AllowSimpleDevicePassword:17,DevicePasswordExpiration:18,DevicePasswordHistory:19,AllowStorageCard:1A,AllowCamera:1B,RequireDeviceEncryption:1C,AllowUnsignedApplications:1D,AllowUnsignedInstallationPackages:1E,MinDevicePasswordComplexCharacters:1F,AllowWiFi:20,AllowTextMessaging:21,AllowPOPIMAPEmail:22,AllowBluetooth:23,AllowIrDA:24,RequireManualSyncWhenRoaming:25,AllowDesktopSync:26,MaxCalendarAgeFilter:27,AllowHTMLEmail:28,MaxEmailAgeFilter:29,MaxEmailBodyTruncationSize:2A,MaxEmailHTMLBodyTruncationSize:2B,RequireSignedSMIMEMessages:2C,RequireEncryptedSMIMEMessages:2D,RequireSignedSMIMEAlgorithm:2E,RequireEncryptionSMIMEAlgorithm:2F,AllowSMIMEEncryptionAlgorithmNegotiation:30,AllowSMIMESoftCerts:31,AllowBrowser:32,AllowConsumerEmail:33,AllowRemoteDesktop:34,AllowInternetSharing:35,UnapprovedInROMApplicationList:36,ApplicationName:37,ApprovedApplicationList:38,Hash:39"),
                [14] = Tokens("Search:05,Stores:06,Store:07,Name:08,Query:09,Options:0A,Range:0B,Status:0C,Response:0D,Result:0E,Properties:0F,Total:10,EqualTo:11,Value:12,And:13,Or:14,FreeText:15,DeepTraversal:17,LongId:18,RebuildResults:19,LessThan:1A,GreaterThan:1B,Schema:1C,Supported:1D,UserName:1E,Password:1F,ConversationId:20,Picture:21,MaxSize:22,MaxPictures:23"),
                [15] = Tokens("DisplayName:05,Phone:06,Office:07,Title:08,Company:09,Alias:0A,FirstName:0B,LastName:0C,EmailAddress:0D,Picture:0E,Status:0F,Data:10"),
                [17] = Tokens("Ping:05,AutdState:06,Status:07,HeartbeatInterval:08,Folders:09,Folder:0A,Id:0B,Class:0C,MaxFolders:0D"),
                [18] = Tokens("Provision:05,Policies:06,Policy:07,PolicyType:08,PolicyKey:09,Data:0A,Status:0B,RemoteWipe:0C,EASProvisionDoc:0D,DevicePasswordEnabled:0E,AlphanumericDevicePasswordRequired:0F,PasswordRecoveryEnabled:10,RequireStorageCardEncryption:11,AttachmentsEnabled:12,MinDevicePasswordLength:13,MaxInactivityTimeDeviceLock:14,MaxDevicePasswordFailedAttempts:15,MaxAttachmentSize:16,AllowSimpleDevicePassword:17,DevicePasswordExpiration:18,DevicePasswordHistory:19,AllowStorageCard:1A,AllowCamera:1B,RequireDeviceEncryption:1C,AllowUnsignedApplications:1D,AllowUnsignedInstallationPackages:1E,MinDevicePasswordComplexCharacters:1F,AllowWiFi:20,AllowTextMessaging:21,AllowPOPIMAPEmail:22,AllowBluetooth:23,AllowIrDA:24,RequireManualSyncWhenRoaming:25,AllowDesktopSync:26,MaxCalendarAgeFilter:27,AllowHTMLEmail:28,MaxEmailAgeFilter:29,MaxEmailBodyTruncationSize:2A,MaxEmailHTMLBodyTruncationSize:2B,RequireSignedSMIMEMessages:2C,RequireEncryptedSMIMEMessages:2D,RequireSignedSMIMEAlgorithm:2E,RequireEncryptionSMIMEAlgorithm:2F,AllowSMIMEEncryptionAlgorithmNegotiation:30,AllowSMIMESoftCerts:31,AllowBrowser:32,AllowConsumerEmail:33,AllowRemoteDesktop:34,AllowInternetSharing:35,UnapprovedInROMApplicationList:36,ApplicationName:37,ApprovedApplicationList:38,Hash:39"),
                [20] = Tokens("ItemOperations:05,Fetch:06,Store:07,Options:08,Range:09,Total:0A,Properties:0B,Data:0C,Status:0D,Response:0E,Version:0F,Schema:10,Part:11,EmptyFolderContents:12,DeleteSubFolders:13,UserName:14,Password:15,Move:16,DstFldId:17,ConversationId:18,MoveAlways:19"),
                [21] = Tokens("UserInformation:05,Get:06,Set:07,Status:08,EmailAddresses:09,SmtpAddress:0A,UserAgent:0B,EnableOutboundSMS:0C,MobileOperator:0D,PrimarySmtpAddress:0E,Accounts:0F,Account:10,AccountId:11,AccountName:12,UserDisplayName:13,SendMail:14,Rules:15,Rule:16,Id:17,State:18,Actions:19,Action:1A,Recipients:1B,Recipient:1C,Exceptions:1D,Exception:1E,StartDate:1F,EndDate:20,Opaque:21,Delete:22,MarkAsRead:23,Change:24,Move:25,CopyToFolder:26,Forward:27,Reply:28,ModifyRecipients:29,SetImportance:2A,MarkImportance:2B,Redirect:2C,Categories:2D,Category:2E"),
                [22] = Tokens("RightsManagementSupport:05,RightsManagementTemplates:06,RightsManagementTemplate:07,RightsManagementLicense:08,EditAllowed:09,ReplyAllowed:0A,ReplyAllAllowed:0B,ForwardAllowed:0C,ModifyRecipientsAllowed:0D,ExtractAllowed:0E,PrintAllowed:0F,ExportAllowed:10,ProgrammaticAccessAllowed:11,RMOwner:12,ContentExpiryDate:13,TemplateID:14,TemplateName:15,TemplateDescription:16,ContentOwner:17,RemoveRightsManagementDistribution:18"),
            };

        public static bool IsEas(IReadOnlyList<KeyValuePair<string, string>> headers, Uri? url)
            => (url?.AbsolutePath.Contains("microsoft-server-activesync", StringComparison.OrdinalIgnoreCase) ?? false)
               || headers.Any(header => string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                   && header.Value.Contains(WbxmlContentType, StringComparison.OrdinalIgnoreCase));

        public static bool TryDecode(byte[] payload, IReadOnlyList<KeyValuePair<string, string>> headers, out string xml)
        {
            xml = string.Empty;
            if (payload.Length == 0)
                return false;

            try
            {
                var bytes = DecodeContent(payload, headers);
                using var output = new StringWriter();
                using var writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false });
                var reader = new Reader(bytes);
                reader.ReadDocument(writer);
                writer.Flush();
                xml = output.ToString();
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
            catch (XmlException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IReadOnlyDictionary<byte, string> Tokens(string values)
            => values.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Split(':', 2))
                .ToDictionary(value => Convert.ToByte(value[1], 16), value => value[0]);

        private static byte[] DecodeContent(byte[] payload, IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            var decoded = headers.Any(header => string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                                              && header.Value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                ? DecodeChunked(payload)
                : payload;
            var encoding = headers.FirstOrDefault(header => string.Equals(header.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(encoding))
                return decoded;

            using var input = new MemoryStream(decoded, writable: false);
            using Stream decompressor = encoding.Split(',')[0].Trim().ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
                "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                _ => throw new InvalidDataException("Unsupported EAS content encoding."),
            };
            using var output = new MemoryStream();
            decompressor.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] DecodeChunked(byte[] payload)
        {
            using var source = new MemoryStream(payload, writable: false);
            using var output = new MemoryStream();
            while (true)
            {
                var line = ReadAsciiLine(source);
                var separator = line.IndexOf(';');
                var lengthText = (separator < 0 ? line : line[..separator]).Trim();
                if (!int.TryParse(lengthText, System.Globalization.NumberStyles.HexNumber, null, out var length) || length < 0)
                    throw new InvalidDataException("Invalid HTTP chunk length.");
                if (length == 0)
                    return output.ToArray();
                var chunk = new byte[length];
                if (source.Read(chunk, 0, length) != length)
                    throw new InvalidDataException("Truncated HTTP chunk.");
                output.Write(chunk, 0, chunk.Length);
                if (source.ReadByte() != '\r' || source.ReadByte() != '\n')
                    throw new InvalidDataException("Invalid HTTP chunk terminator.");
            }
        }

        private static string ReadAsciiLine(Stream stream)
        {
            using var line = new MemoryStream();
            int current;
            while ((current = stream.ReadByte()) >= 0)
            {
                if (current == '\r' && stream.ReadByte() == '\n')
                    return Encoding.ASCII.GetString(line.ToArray());
                line.WriteByte((byte)current);
            }
            throw new InvalidDataException("Truncated HTTP chunk header.");
        }

        private sealed class Reader
        {
            private readonly byte[] _bytes;
            private int _offset;
            private int _page;
            private byte[] _stringTable = Array.Empty<byte>();

            public Reader(byte[] bytes) => _bytes = bytes;

            public void ReadDocument(XmlWriter writer)
            {
                Require(4);
                _offset++; // Version
                var publicId = ReadMbUInt();
                if (publicId == 0)
                    ReadMbUInt(); // Public identifier string-table index
                if (ReadMbUInt() != 106)
                    throw new InvalidDataException("EAS WBXML must use UTF-8.");
                var tableLength = ReadMbUInt();
                Require(tableLength);
                _stringTable = _bytes.AsSpan(_offset, tableLength).ToArray();
                _offset += tableLength;

                writer.WriteStartDocument();
                while (_offset < _bytes.Length)
                    ReadToken(writer);
                writer.WriteEndDocument();
            }

            private void ReadToken(XmlWriter writer)
            {
                var token = ReadByte();
                switch (token)
                {
                    case 0x00:
                        _page = ReadByte();
                        return;
                    case 0x01:
                        writer.WriteEndElement();
                        return;
                    case 0x02:
                        writer.WriteCharEntity((char)ReadMbUInt());
                        return;
                    case 0x03:
                        writer.WriteString(ReadInlineString());
                        return;
                    case 0x83:
                        writer.WriteString(ReadTableString(ReadMbUInt()));
                        return;
                    case 0xC3:
                        writer.WriteBase64(ReadBytes(ReadMbUInt()), 0, _lastReadLength);
                        return;
                    case 0x40:
                    case 0x41:
                    case 0x42:
                        writer.WriteString(ReadInlineString());
                        return;
                    case 0x80:
                    case 0x81:
                    case 0x82:
                        ReadMbUInt();
                        return;
                    case 0xC0:
                    case 0xC1:
                    case 0xC2:
                        return;
                    default:
                        if ((token & 0x3F) == 0x04)
                            throw new InvalidDataException("Literal WBXML tags are unsupported.");
                        if ((token & 0x80) != 0)
                            throw new InvalidDataException("EAS WBXML attributes are unsupported.");
                        var name = Tags.TryGetValue(_page, out var page) && page.TryGetValue((byte)(token & 0x3F), out var tag)
                            ? tag
                            : $"Page{_page}_Token{token & 0x3F:X2}";
                        writer.WriteStartElement(name);
                        if ((token & 0x40) == 0)
                            writer.WriteEndElement();
                        return;
                }
            }

            private int _lastReadLength;
            private byte[] ReadBytes(int length)
            {
                Require(length);
                var result = _bytes.AsSpan(_offset, length).ToArray();
                _offset += length;
                _lastReadLength = length;
                return result;
            }

            private string ReadInlineString()
            {
                var start = _offset;
                while (ReadByte() != 0) { }
                return Encoding.UTF8.GetString(_bytes, start, _offset - start - 1);
            }

            private string ReadTableString(int index)
            {
                if (index < 0 || index >= _stringTable.Length)
                    throw new InvalidDataException("Invalid WBXML string-table index.");
                var end = index;
                while (end < _stringTable.Length && _stringTable[end] != 0)
                    end++;
                return Encoding.UTF8.GetString(_stringTable, index, end - index);
            }

            private int ReadMbUInt()
            {
                var value = 0;
                byte current;
                do
                {
                    current = ReadByte();
                    if (value > (int.MaxValue >> 7))
                        throw new InvalidDataException("WBXML integer is too large.");
                    value = (value << 7) | (current & 0x7F);
                } while ((current & 0x80) != 0);
                return value;
            }

            private byte ReadByte()
            {
                Require(1);
                return _bytes[_offset++];
            }

            private void Require(int count)
            {
                if (count < 0 || _offset > _bytes.Length - count)
                    throw new InvalidDataException("Truncated WBXML data.");
            }
        }
    }
}
