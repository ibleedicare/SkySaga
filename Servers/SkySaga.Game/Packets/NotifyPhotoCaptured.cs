using System;

using RakNet;

using SkySaga.Game.Extensions;

namespace SkySaga.Game.Packets;

/// <summary>
/// The client tells the server it captured a photo (space bar in camera mode) and waits
/// for a <see cref="PhotoValidated"/> before it uploads the image.
/// </summary>
/// <remarks>
/// Layout from the client's serializer (FUN_00791e60, packet id 0x96):
/// <code>
/// clientPhotoID   compressed uint   (see BitStreamExtensions.ReadCompressedUInt)
/// position        3 floats          where the shot was taken - not needed to validate
/// direction       3 floats
/// isAvatarPhoto   1 bit
/// </code>
/// Only clientPhotoID is read here; the server just needs to echo it back in the
/// validation so the client can match the reply to the pending capture.
/// </remarks>
public static class NotifyPhotoCaptured
{
    public static bool Handle(Connection connection, BitStream bitStream)
    {
        var clientPhotoId = bitStream.ReadCompressedUInt();

        var officialUuid = Util.NewGuid();
        var uploadToken = Util.NewGuid();

        Console.WriteLine($"[photo] captured clientPhotoID={clientPhotoId} -> validating as {officialUuid}");

        connection.Send(new PhotoValidated
        {
            ClientPhotoId = clientPhotoId,
            OfficialUuid = officialUuid,
            UploadToken = uploadToken
        });

        return true;
    }
}
