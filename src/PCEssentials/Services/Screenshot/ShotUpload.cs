using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PCEssentials.Services.Screenshot;

/// The upload was refused; the message is fit to show.
public sealed class ShotUploadException(string message) : Exception(message);

/// Share links, like Lightshot's upload: the image goes to ImgBB under the user's own free API key
/// (https://api.imgbb.com), and the direct link to it comes back. Uploads are listed in that
/// ImgBB account, where they can be deleted.
public static class ShotUpload
{
    /// ImgBB refuses anything larger.
    public const int MaxBytes = 32 * 1024 * 1024;

    /// The lifetimes offered in Settings, in seconds; 0 keeps the image until it is deleted.
    public static readonly (int Seconds, string Name)[] Lifetimes =
    [
        (0, "Never expire"),
        (3600, "Expire after 1 hour"),
        (86400, "Expire after 1 day"),
        (604800, "Expire after 1 week"),
        (2592000, "Expire after 30 days"),
    ];

    /// ImgBB takes 60 seconds to 180 days; 0 or less means no expiry.
    public static int Expiration(int seconds) => seconds <= 0 ? 0 : Math.Clamp(seconds, 60, 15552000);

    /// A pasted key without the spaces or line breaks that tend to come along with it.
    public static string CleanKey(string? key) => new((key ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// ImgBB's documented form: the key and the expiry in the query, the image in the body.
    public static Uri Endpoint(string key, int expirySeconds)
    {
        var url = "https://api.imgbb.com/1/upload?key=" + Uri.EscapeDataString(CleanKey(key));
        var expiry = Expiration(expirySeconds);
        return new Uri(expiry > 0 ? $"{url}&expiration={expiry}" : url);
    }

    /// Reads ImgBB's JSON answer. True with the image's direct https link, otherwise false with
    /// ImgBB's own reason (or a plain one), so an error is never copied as if it were a link.
    public static bool TryParse(string? body, out string link, out string error)
    {
        link = "";
        error = "ImgBB sent an answer that could not be read.";
        try
        {
            using var doc = JsonDocument.Parse(body ?? "");
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String && message.GetString() is { Length: > 0 and <= 200 } text)
                    error = text;
                return false;
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
            if (!data.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) return false;
            if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            link = uri.AbsoluteUri;
            error = "";
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static MultipartFormDataContent Form(byte[] data, string fileName)
    {
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png");
        return new MultipartFormDataContent { { file, "image", fileName } };
    }

    /// Uploads the image and returns its link. Throws ShotUploadException when ImgBB refuses, and
    /// the usual HttpRequestException or TaskCanceledException when it cannot be reached.
    public static async Task<string> SendAsync(HttpClient http, byte[] data, string fileName, string key, int expirySeconds, CancellationToken cancel = default)
    {
        using var form = Form(data, fileName);
        using var response = await http.PostAsync(Endpoint(key, expirySeconds), form, cancel).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (TryParse(body, out var link, out var error)) return link;
        throw new ShotUploadException(error);
    }
}
