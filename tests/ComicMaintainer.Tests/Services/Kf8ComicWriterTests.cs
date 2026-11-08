using ComicMaintainer.Core.Services.Kf8;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Verifies that <see cref="Kf8ComicWriter"/> writes page images straight from
/// their source one at a time, instead of holding every page of the book in
/// memory, and that it refuses books the PalmDB format cannot address rather
/// than writing a corrupt file.
/// </summary>
public sealed class Kf8ComicWriterTests : IDisposable
{
    private readonly string _workDir;

    public Kf8ComicWriterTests()
    {
        _workDir = Path.Join(Path.GetTempPath(), $"kf8-writer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Write_OpensEachPageImageOnceWhileWritingAndNeverTwoAtATime()
    {
        var images = Enumerable.Range(1, 5)
            .Select(i => Enumerable.Range(0, 1000 * i).Select(b => (byte)(b * i)).ToArray())
            .ToList();
        var tracker = new OpenTracker();
        var path = Path.Join(_workDir, "book.azw3");
        var pages = images
            .Select((bytes, index) => new Kf8Page(
                bytes.Length,
                () => tracker.Open(index, bytes, () => File.Exists(path)),
                "image/jpeg",
                10,
                20,
                $"Page {index + 1}"))
            .ToList();

        Kf8ComicWriter.Write(new Kf8Book("Book", pages), path);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, tracker.OpenedInOrder);
        Assert.Equal(1, tracker.MaxOpenAtOnce);
        Assert.All(tracker.OpenedWhileFileExisted, Assert.True);
        Assert.Equal(0, tracker.OpenNow);

        var book = Azw3Reader.Read(path);
        Assert.Equal(images, book.Resources);
        Assert.Equal(5, book.RebuildDocuments().Count);
    }

    [Fact]
    public void Write_RefusesABookPastTheFourGigabyteAddressLimitBeforeCreatingTheFile()
    {
        var path = Path.Join(_workDir, "huge.azw3");
        var pages = Enumerable.Range(1, 3)
            .Select(i => new Kf8Page(
                (long)int.MaxValue,
                () => throw new InvalidOperationException("No page may be read for a book that cannot be written."),
                "image/jpeg",
                10,
                20,
                $"Page {i}"))
            .ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => Kf8ComicWriter.Write(new Kf8Book("Huge", pages), path));

        Assert.Contains("cannot address more than 4 GB", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Write_CountsTheTextAndIndexRecordsAgainstTheRecordLimit()
    {
        // Few enough pages to pass the early check on the page count alone,
        // but their text and index records push the book over the limit.
        var path = Path.Join(_workDir, "text-heavy.azw3");
        var pages = Enumerable.Range(1, 60_000)
            .Select(i => new Kf8Page(
                1,
                () => throw new InvalidOperationException("No page may be read for a book that cannot be written."),
                "image/jpeg",
                10,
                20,
                "Page"))
            .ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => Kf8ComicWriter.Write(new Kf8Book("Text heavy", pages), path));

        Assert.Contains("too many pages for the AZW3 format", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Write_RefusesMorePagesThanThePalmDatabaseCanIndex()
    {
        var path = Path.Join(_workDir, "long.azw3");
        var pages = Enumerable.Range(1, Kf8ComicWriter.MaxRecords)
            .Select(i => new Kf8Page(
                1,
                () => throw new InvalidOperationException("No page may be read for a book that cannot be written."),
                "image/jpeg",
                10,
                20,
                "Page"))
            .ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => Kf8ComicWriter.Write(new Kf8Book("Long", pages), path));

        Assert.Contains("too many pages for the AZW3 format", ex.Message);
        Assert.Contains("at most 65,535", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Write_KeepsPageAnchorsAndTheirSelectorsValidInAVeryLongBook()
    {
        // Past both the page where an anchor number used to overflow (2,148)
        // and the page where the selectors outgrow one 64K record.
        const int PageCount = 6_000;
        var path = Path.Join(_workDir, "long-valid.azw3");
        var pages = Enumerable.Range(1, PageCount)
            .Select(i => new Kf8Page(4, () => new MemoryStream(new byte[4]), "image/jpeg", 10, 20, "Page"))
            .ToList();

        Kf8ComicWriter.Write(new Kf8Book("Long", pages), path);

        var book = Azw3Reader.Read(path);
        var documents = book.RebuildDocuments();
        Assert.Equal(PageCount, documents.Count);

        var anchors = documents
            .Select(document => System.Text.RegularExpressions.Regex.Matches(document, "aid=\"([0-9A-V]+)\"")
                .Select(match => match.Groups[1].Value)
                .ToList())
            .ToList();
        Assert.All(anchors, pageAnchors => Assert.Equal(2, pageAnchors.Count));
        Assert.Equal(PageCount * 2, anchors.SelectMany(pageAnchors => pageAnchors).Distinct().Count());

        // The reader splits on these: each selector must name its own page's body.
        Assert.True(book.ChunkCncxRecordCount > 1);
        Assert.Equal(
            anchors.Select(pageAnchors => $"P-//*[@aid='{pageAnchors[0]}']"),
            book.ChunkSelectors);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(101)]
    public void Write_RejectsAPageImageWhoseLengthDiffersFromTheDeclaredOne(int actualLength)
    {
        var path = Path.Join(_workDir, "mismatch.azw3");
        var pages = new[]
        {
            new Kf8Page(100, () => new MemoryStream(new byte[actualLength]), "image/jpeg", 10, 20, "Page 1")
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Kf8ComicWriter.Write(new Kf8Book("Mismatch", pages), path));

        Assert.Equal("The image of Page 1 changed size while the AZW3 book was being written.", ex.Message);
    }

    [Fact]
    public void Write_StopsBetweenRecordsWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var opened = 0;
        var path = Path.Join(_workDir, "cancelled.azw3");
        var pages = Enumerable.Range(1, 3)
            .Select(i => new Kf8Page(
                4,
                () =>
                {
                    opened++;
                    cancellation.Cancel();
                    return new MemoryStream(new byte[4]);
                },
                "image/jpeg",
                10,
                20,
                $"Page {i}"))
            .ToList();

        Assert.Throws<OperationCanceledException>(() =>
            Kf8ComicWriter.Write(new Kf8Book("Cancelled", pages), path, cancellation.Token));

        Assert.Equal(1, opened);
    }

    /// <summary>Records when page images are opened and how many are open at once.</summary>
    private sealed class OpenTracker
    {
        public List<int> OpenedInOrder { get; } = new();

        public List<bool> OpenedWhileFileExisted { get; } = new();

        public int OpenNow { get; private set; }

        public int MaxOpenAtOnce { get; private set; }

        public Stream Open(int index, byte[] bytes, Func<bool> fileExists)
        {
            OpenedInOrder.Add(index);
            OpenedWhileFileExisted.Add(fileExists());
            OpenNow++;
            MaxOpenAtOnce = Math.Max(MaxOpenAtOnce, OpenNow);
            return new TrackedStream(bytes, () => OpenNow--);
        }
    }

    private sealed class TrackedStream : MemoryStream
    {
        private Action? _onDispose;

        public TrackedStream(byte[] bytes, Action onDispose)
            : base(bytes, writable: false)
        {
            _onDispose = onDispose;
        }

        protected override void Dispose(bool disposing)
        {
            _onDispose?.Invoke();
            _onDispose = null;
            base.Dispose(disposing);
        }
    }
}
