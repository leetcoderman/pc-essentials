using PCEssentials.Services.Screenshot;
using Xunit;

namespace PCEssentials.Tests;

public class ShotGeometryTests
{
    private static readonly PxRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void FromCornersAcceptsEitherOrderAndIncludesBothCorners()
    {
        Assert.Equal(new PxRect(10, 20, 91, 81), PxRect.FromCorners(100, 100, 10, 20));
        Assert.Equal(new PxRect(10, 20, 91, 81), PxRect.FromCorners(10, 20, 100, 100));
    }

    [Fact]
    public void IntersectClipsToTheOtherRectAndIsEmptyWhenApart()
    {
        Assert.Equal(new PxRect(0, 0, 50, 50), new PxRect(-50, -50, 100, 100).Intersect(Screen));
        Assert.True(new PxRect(3000, 0, 10, 10).Intersect(Screen).IsEmpty);
    }

    [Theory]
    [InlineData(100, 100, ShotHandle.NW)]
    [InlineData(300, 100, ShotHandle.NE)]
    [InlineData(100, 300, ShotHandle.SW)]
    [InlineData(300, 300, ShotHandle.SE)]
    [InlineData(200, 98, ShotHandle.N)]
    [InlineData(200, 303, ShotHandle.S)]
    [InlineData(97, 200, ShotHandle.W)]
    [InlineData(302, 200, ShotHandle.E)]
    [InlineData(200, 200, ShotHandle.Inside)]
    [InlineData(400, 200, ShotHandle.None)]
    [InlineData(200, 90, ShotHandle.None)]
    public void HitTestFindsHandlesWithinTheGrip(double x, double y, ShotHandle expected)
    {
        var sel = new PxRect(100, 100, 200, 200);
        Assert.Equal(expected, ShotGeometry.HitTest(sel, x, y, 6));
    }

    [Fact]
    public void HitTestOnEmptySelectionIsNone() =>
        Assert.Equal(ShotHandle.None, ShotGeometry.HitTest(default, 0, 0, 6));

    [Fact]
    public void DraggingACornerResizesFromTheOppositeCorner()
    {
        var sel = new PxRect(100, 100, 200, 200);
        Assert.Equal(new PxRect(100, 100, 250, 230), ShotGeometry.Drag(sel, ShotHandle.SE, 50, 30, Screen));
        Assert.Equal(new PxRect(80, 90, 220, 210), ShotGeometry.Drag(sel, ShotHandle.NW, -20, -10, Screen));
    }

    [Fact]
    public void DraggingAnEdgeOnlyChangesThatEdge()
    {
        var sel = new PxRect(100, 100, 200, 200);
        Assert.Equal(new PxRect(100, 100, 240, 200), ShotGeometry.Drag(sel, ShotHandle.E, 40, 999, Screen));
        Assert.Equal(new PxRect(100, 60, 200, 240), ShotGeometry.Drag(sel, ShotHandle.N, 999, -40, Screen));
    }

    [Fact]
    public void DraggingAnEdgePastTheOppositeOneFlipsTheSelection()
    {
        var sel = new PxRect(100, 100, 200, 200);
        // The right edge (x = 300) dragged to x = 50 goes past the left edge (x = 100).
        Assert.Equal(new PxRect(50, 100, 50, 200), ShotGeometry.Drag(sel, ShotHandle.E, -250, 0, Screen));
    }

    [Fact]
    public void ResizingStaysWithinBounds()
    {
        var sel = new PxRect(100, 100, 200, 200);
        Assert.Equal(new PxRect(0, 0, 300, 300), ShotGeometry.Drag(sel, ShotHandle.NW, -500, -500, Screen));
        Assert.Equal(new PxRect(100, 100, 1820, 980), ShotGeometry.Drag(sel, ShotHandle.SE, 5000, 5000, Screen));
    }

    [Fact]
    public void MovingKeepsTheSizeAndStopsAtTheEdges()
    {
        var sel = new PxRect(100, 100, 200, 200);
        Assert.Equal(new PxRect(150, 80, 200, 200), ShotGeometry.Drag(sel, ShotHandle.Inside, 50, -20, Screen));
        Assert.Equal(new PxRect(1720, 0, 200, 200), ShotGeometry.Move(sel, 5000, -5000, Screen));
    }

    [Fact]
    public void MovingWorksAcrossMonitorsWithNegativeCoordinates()
    {
        var virtualScreen = new PxRect(-2560, -200, 4480, 1440);
        var sel = new PxRect(-100, 100, 300, 300);
        Assert.Equal(new PxRect(-2560, 100, 300, 300), ShotGeometry.Move(sel, -5000, 0, virtualScreen));
    }

    [Fact]
    public void ToolbarsGoRightAndBelowLikeLightshot()
    {
        var sel = new PxRect(100, 100, 400, 300);
        var (tools, actions) = ShotGeometry.PlaceToolbars(sel, Screen, (40, 340), (200, 40), 6);
        Assert.Equal((506, 60), tools);          // right of the selection, bottom aligned
        Assert.Equal((300, 406), actions);       // below the selection, right aligned
    }

    [Fact]
    public void ToolbarsFlipSidesNearTheMonitorEdges()
    {
        var sel = new PxRect(1500, 700, 418, 378); // touches the right and bottom edges
        var (tools, actions) = ShotGeometry.PlaceToolbars(sel, Screen, (40, 340), (200, 40), 6);
        Assert.Equal((1454, 738), tools);        // left side instead
        Assert.Equal((1718, 654), actions);      // above instead
    }

    [Fact]
    public void ToolbarsGoInsideAFullScreenSelection()
    {
        var (tools, actions) = ShotGeometry.PlaceToolbars(Screen, Screen, (40, 340), (200, 40), 6);
        Assert.Equal((1874, 740), tools);
        Assert.Equal((1720, 1034), actions);
    }

    [Fact]
    public void ToolbarsStayOnTheMonitorForATinySelectionInACorner()
    {
        var (tools, actions) = ShotGeometry.PlaceToolbars(new PxRect(0, 0, 20, 20), Screen, (40, 340), (200, 40), 6);
        Assert.Equal(0, tools.Y);
        Assert.Equal(0, actions.X);
    }

    [Fact]
    public void SizeLabelSitsAboveOrInsideTheTopLeftCorner()
    {
        Assert.Equal((100, 72), ShotGeometry.PlaceSizeLabel(new PxRect(100, 100, 50, 50), Screen, (80, 24), 4));
        Assert.Equal((100, 14), ShotGeometry.PlaceSizeLabel(new PxRect(100, 10, 50, 50), Screen, (80, 24), 4));
    }
}

public class ShotModelTests
{
    private static ShotShape Shape(ShotTool tool, params (double X, double Y)[] points)
    {
        var s = new ShotShape { Tool = tool, Color = 0xFFFF0000, Width = 3 };
        s.Points.AddRange(points);
        return s;
    }

    [Fact]
    public void UndoRemovesTheNewestShapeAndStopsWhenEmpty()
    {
        var shapes = new ShotShapes();
        var first = Shape(ShotTool.Line, (0, 0), (50, 50));
        var second = Shape(ShotTool.Rectangle, (10, 10), (60, 40));
        shapes.Add(first);
        shapes.Add(second);
        Assert.True(shapes.Undo());
        Assert.Same(first, Assert.Single(shapes.Items));
        Assert.True(shapes.Undo());
        Assert.False(shapes.Undo());
    }

    [Fact]
    public void ClicksWithoutADragAreNotKept()
    {
        var shapes = new ShotShapes();
        shapes.Add(Shape(ShotTool.Rectangle, (10, 10), (11, 11)));
        shapes.Add(Shape(ShotTool.Text, (10, 10)));
        Assert.Equal(0, shapes.Count);
    }

    [Fact]
    public void APenClickIsADot()
    {
        var shapes = new ShotShapes();
        shapes.Add(Shape(ShotTool.Pen, (10, 10)));
        Assert.Equal(1, shapes.Count);
    }

    [Fact]
    public void TextWithContentIsKept()
    {
        var shapes = new ShotShapes();
        var text = Shape(ShotTool.Text, (10, 10));
        text.Text = "hello";
        shapes.Add(text);
        Assert.Equal(1, shapes.Count);
    }

    [Fact]
    public void ShapeBoundsSpanStartAndEnd() =>
        Assert.Equal(new PxRect(20, 10, 81, 41), Shape(ShotTool.Blur, (100, 50), (20, 10)).Bounds);

    [Fact]
    public void PixelateAveragesEachBlock()
    {
        // 4×2 image: left 2×2 block black and white, right block all grey.
        const int w = 4, h = 2, stride = w * 4;
        var px = new byte[stride * h];
        void Set(int x, int y, byte v) { var i = y * stride + x * 4; px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        Set(0, 0, 0); Set(1, 0, 255); Set(0, 1, 255); Set(1, 1, 0);
        Set(2, 0, 100); Set(3, 0, 100); Set(2, 1, 100); Set(3, 1, 100);
        var result = ShotImage.Pixelate(px, stride, new PxRect(0, 0, w, h), 2);
        Assert.Equal(127, result[0]);
        Assert.Equal(127, result[1 * 4]);
        Assert.Equal(127, result[(1 * w + 1) * 4]);
        Assert.Equal(100, result[2 * 4]);
        Assert.Equal(255, result[3]);
    }

    [Fact]
    public void PixelateWorksOnARegionAndPartialBlocks()
    {
        const int w = 5, h = 3, stride = w * 4;
        var px = new byte[stride * h];
        for (var i = 0; i < px.Length; i += 4) px[i] = (byte)(i / 4); // blue = pixel index
        var result = ShotImage.Pixelate(px, stride, new PxRect(1, 1, 3, 2), 2);
        Assert.Equal(3 * 2 * 4, result.Length);
        Assert.Equal((6 + 7 + 11 + 12) / 4, result[0]);   // 2×2 block
        Assert.Equal((8 + 13) / 2, result[2 * 4]);        // 1×2 leftover column
    }
}

public class ShotFilesTests
{
    private static readonly DateTime When = new(2026, 11, 1, 9, 5, 7);

    [Fact]
    public void DefaultPatternUsesTheCaptureTime() =>
        Assert.Equal("Screenshot 2026-11-01 09.05.07", ShotFiles.Name(ShotFiles.DefaultPattern, When, _ => false, ".png"));

    [Fact]
    public void EmptyPatternFallsBackToTheDefault() =>
        Assert.Equal("Screenshot 2026-11-01 09.05.07", ShotFiles.Name("  ", When, _ => false, ".png"));

    [Fact]
    public void CounterPicksTheFirstUnusedNumber()
    {
        var taken = new HashSet<string> { "Shot_1.png", "Shot_2.png" };
        Assert.Equal("Shot_3", ShotFiles.Name("Shot_{n}", When, taken.Contains, ".png"));
    }

    [Fact]
    public void ExistingNameWithoutCounterGetsANumberAppended()
    {
        var taken = new HashSet<string> { "Same.png", "Same (2).png" };
        Assert.Equal("Same (3)", ShotFiles.Name("Same", When, taken.Contains, ".png"));
    }

    [Fact]
    public void ForbiddenCharactersAreReplaced() =>
        Assert.Equal("a-b-c 09-05", ShotFiles.Name("a/b:c {HH:mm}", When, _ => false, ".png"));

    [Fact]
    public void UnclosedBraceIsLiteral() =>
        Assert.Equal("x {yyyy", ShotFiles.Name("x {yyyy", When, _ => false, ".png"));

    [Theory]
    [InlineData("png", ".png")]
    [InlineData("jpg", ".jpg")]
    [InlineData("JPEG", ".jpg")]
    [InlineData("bmp", ".bmp")]
    [InlineData("whatever", ".png")]
    public void ExtensionPerFormat(string format, string ext) => Assert.Equal(ext, ShotFiles.Extension(format));

    [Theory]
    [InlineData(@"C:\a\b.JPEG", "jpg")]
    [InlineData(@"C:\a\b.png", "png")]
    [InlineData(@"C:\a\b.bmp", "bmp")]
    [InlineData(@"C:\a\b.gif", "png")]
    [InlineData(@"C:\a\b", "png")]
    public void FormatFromPathFollowsTheExtension(string path, string format) =>
        Assert.Equal(format, ShotFiles.FormatFromPath(path, "png"));
}

public class ShotUploadTests
{
    private sealed class FakeHost(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string Sent = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Request = request;
            Sent = await request.Content!.ReadAsStringAsync(cancel);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private const string Ok = """{"data":{"id":"2ndCYJK","url_viewer":"https://ibb.co/2ndCYJK","url":"https://i.ibb.co/w04Prt6/screenshot.png","delete_url":"https://ibb.co/2ndCYJK/670a7e48ddcb85ac340c717a41047e5c"},"success":true,"status":200}""";
    private const string BadKey = """{"status_code":400,"error":{"message":"Invalid API v1 key.","code":100},"status_txt":"Bad Request"}""";

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(10, 60)]
    [InlineData(3600, 3600)]
    [InlineData(99999999, 15552000)]
    public void ExpirationStaysWithinWhatImgBBTakes(int seconds, int expected) =>
        Assert.Equal(expected, ShotUpload.Expiration(seconds));

    [Fact]
    public void EndpointCarriesTheCleanedKeyAndOnlyARealExpiry()
    {
        Assert.Equal("https://api.imgbb.com/1/upload?key=abc123", ShotUpload.Endpoint(" abc 123\r\n", 0).AbsoluteUri);
        Assert.Equal("https://api.imgbb.com/1/upload?key=abc123&expiration=86400", ShotUpload.Endpoint("abc123", 86400).AbsoluteUri);
        Assert.Equal("https://api.imgbb.com/1/upload?key=a%26b", ShotUpload.Endpoint("a&b", 0).AbsoluteUri);
    }

    [Fact]
    public void ParseReturnsTheDirectLink()
    {
        Assert.True(ShotUpload.TryParse(Ok, out var link, out var error));
        Assert.Equal("https://i.ibb.co/w04Prt6/screenshot.png", link);
        Assert.Equal("", error);
    }

    [Fact]
    public void ParseGivesImgBBsReasonWhenItRefuses()
    {
        Assert.False(ShotUpload.TryParse(BadKey, out var link, out var error));
        Assert.Equal("", link);
        Assert.Equal("Invalid API v1 key.", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("[]")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"url":"http://i.ibb.co/x/y.png"}}""")]
    [InlineData("""{"data":{"url":"not a link"}}""")]
    [InlineData("""{"data":{"url":42}}""")]
    public void ParseNeverTreatsAnythingElseAsALink(string? body)
    {
        Assert.False(ShotUpload.TryParse(body, out var link, out var error));
        Assert.Equal("", link);
        Assert.NotEqual("", error);
    }

    [Fact]
    public async Task SendPostsTheImageAndReturnsTheLink()
    {
        var host = new FakeHost(System.Net.HttpStatusCode.OK, Ok);
        using var http = new HttpClient(host);
        var link = await ShotUpload.SendAsync(http, [1, 2, 3], "screenshot.png", "abc123", 3600);
        Assert.Equal("https://i.ibb.co/w04Prt6/screenshot.png", link);
        Assert.Equal(HttpMethod.Post, host.Request!.Method);
        Assert.Equal("https://api.imgbb.com/1/upload?key=abc123&expiration=3600", host.Request.RequestUri!.AbsoluteUri);
        Assert.Contains("name=image", host.Sent);
        Assert.Contains("filename=screenshot.png", host.Sent);
        Assert.Contains("image/png", host.Sent);
    }

    [Fact]
    public async Task SendThrowsImgBBsReasonWhenItRefuses()
    {
        using var http = new HttpClient(new FakeHost(System.Net.HttpStatusCode.BadRequest, BadKey));
        var ex = await Assert.ThrowsAsync<ShotUploadException>(() => ShotUpload.SendAsync(http, [1], "screenshot.jpg", "nope", 0));
        Assert.Equal("Invalid API v1 key.", ex.Message);
    }
}

public class SnippingKeyTests
{
    [Theory]
    [InlineData(1, 19045, true)]
    [InlineData(0, 26200, false)]
    [InlineData(null, 26200, true)]   // unset on current Windows 11: on by default
    [InlineData(null, 22621, true)]
    [InlineData(null, 22000, false)]  // unset on Windows 11 21H2 and Windows 10: off
    [InlineData(null, 19045, false)]
    public void UnsetValueFollowsTheWindowsDefault(int? value, int build, bool on) =>
        Assert.Equal(on, SnippingKey.IsOn(value, build));
}
