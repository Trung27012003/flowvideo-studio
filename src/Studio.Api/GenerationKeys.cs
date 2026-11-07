using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Studio;

public static class GenerationKeys
{
    static string Hash(object value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))
        );

    public static string Image(
        Guid sceneId,
        string prompt,
        string model,
        string size,
        string quality
    ) =>
        Hash(
            new
            {
                sceneId,
                prompt,
                model,
                size,
                quality,
            }
        );

    public static string Video(
        Guid sceneId,
        string prompt,
        string model,
        string size,
        string quality,
        int seconds,
        Guid? referenceId
    ) =>
        Hash(
            new
            {
                sceneId,
                prompt,
                model,
                size,
                quality,
                seconds,
                referenceId,
            }
        );
}
