using RakNet;

using SkySaga.Game.Extensions;
using SkySaga.Game.Interfaces;

namespace SkySaga.Game.Packets;

/// <summary>
/// Reply to <see cref="NotifyPhotoCaptured"/>: tells the client its captured photo was
/// accepted, and hands it the id and token it needs to upload the image over HTTP.
/// </summary>
/// <remarks>
/// Layout reversed from the client's deserializer (FUN_0073f880, logs "RPCPhotoValidated"):
/// <code>
/// clientPhotoID   compressed uint  (echoed from the capture so the client matches it up)
/// officialUUID    string           the server's photo id; the client PUTs the image to
///                                  /api/binary-storage/photos/&lt;officialUUID&gt;/_upload
/// uploadToken     string           passed along with the upload
/// </code>
/// Until this arrives the client keeps the photo in its "waiting" queue and never uploads.
/// </remarks>
public class PhotoValidated : ISerializablePacket
{
    public uint ClientPhotoId;

    public required string OfficialUuid;
    public required string UploadToken;

    public BitStream Serialize()
    {
        var bitStream = new BitStream();

        bitStream.WritePacketId(PacketId.PhotoValidated);

        bitStream.WriteCompressedUInt(ClientPhotoId);

        bitStream.WriteString(OfficialUuid);
        bitStream.WriteString(UploadToken);

        return bitStream;
    }
}
