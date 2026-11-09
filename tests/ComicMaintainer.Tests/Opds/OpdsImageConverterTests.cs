using ComicMaintainer.WebApi.Opds;
using ImageMagick;

namespace ComicMaintainer.Tests.Opds;

/// <summary>How images are converted for OPDS clients: only when needed, and only a few at a time.</summary>
public sealed class OpdsImageConverterTests
{
    private static readonly (byte[] Data, string ContentType) Converted = ([9], OpdsMediaTypes.Jpeg);

    [Fact]
    public async Task AnImageAlreadyOfTheTargetTypeIsNotConverted()
    {
        var calls = 0;
        var converter = new OpdsImageConverter(_ =>
        {
            calls++;
            return Converted;
        });
        byte[] data = [1, 2, 3];

        var result = await converter.ConvertAsync(data, "IMAGE/PNG", OpdsMediaTypes.Png, default);

        Assert.Same(data, result.Data);
        Assert.Equal("IMAGE/PNG", result.ContentType);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ARealImageBecomesAJpeg()
    {
        using var image = new MagickImage(MagickColors.Red, 4, 4) { Format = MagickFormat.WebP };

        var result = await new OpdsImageConverter().ConvertAsync(image.ToByteArray(), "image/webp", OpdsMediaTypes.Jpeg, default);

        Assert.Equal(OpdsMediaTypes.Jpeg, result.ContentType);
        Assert.Equal(MagickFormat.Jpeg, new MagickImageInfo(result.Data).Format);
    }

    [Fact]
    public async Task AnImageThatCannotBeConvertedIsPassedThrough()
    {
        byte[] notAnImage = [1, 2, 3];

        var result = await new OpdsImageConverter().ConvertAsync(notAnImage, "image/webp", OpdsMediaTypes.Jpeg, default);

        Assert.Same(notAnImage, result.Data);
        Assert.Equal("image/webp", result.ContentType);
    }

    [Fact]
    public async Task NoMoreThanTheLimitRunAtOnceAndTheRestWaitTheirTurn()
    {
        var running = 0;
        var mostAtOnce = 0;
        var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var converter = new OpdsImageConverter(_ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref mostAtOnce, now);
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(30));
            Interlocked.Decrement(ref running);
            return Converted;
        });

        var conversions = Enumerable.Range(0, OpdsImageConverter.MaxConcurrentConversions + 3)
            .Select(_ => converter.ConvertAsync([1], "image/webp", OpdsMediaTypes.Jpeg, default))
            .ToArray();

        for (var i = 0; i < OpdsImageConverter.MaxConcurrentConversions; i++)
        {
            Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30)), "A conversion should have started");
        }

        // The slots are taken: nobody else gets in while they are.
        Assert.False(await entered.WaitAsync(TimeSpan.FromMilliseconds(200)), "A conversion started past the limit");

        release.Set();
        var results = await Task.WhenAll(conversions);

        Assert.All(results, result => Assert.Equal(OpdsMediaTypes.Jpeg, result.ContentType));
        Assert.Equal(OpdsImageConverter.MaxConcurrentConversions, mostAtOnce);
    }

    [Fact]
    public async Task AClientThatLeavesWhileWaitingGivesUpWithoutTakingASlot()
    {
        var started = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var converter = new OpdsImageConverter(_ =>
        {
            started.Release();
            release.Wait(TimeSpan.FromSeconds(30));
            return Converted;
        });
        var busy = Enumerable.Range(0, OpdsImageConverter.MaxConcurrentConversions)
            .Select(_ => converter.ConvertAsync([1], "image/webp", OpdsMediaTypes.Jpeg, default))
            .ToArray();
        for (var i = 0; i < busy.Length; i++)
        {
            Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)), "A conversion should have started");
        }

        using var leaving = new CancellationTokenSource();
        var waiting = converter.ConvertAsync([1], "image/webp", OpdsMediaTypes.Jpeg, leaving.Token);
        await leaving.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        release.Set();
        await Task.WhenAll(busy);
        var after = await converter.ConvertAsync([1], "image/webp", OpdsMediaTypes.Jpeg, default);
        Assert.Equal(OpdsMediaTypes.Jpeg, after.ContentType);
    }

    [Fact]
    public async Task AConversionThatFailsGivesItsSlotBack()
    {
        var calls = 0;
        var converter = new OpdsImageConverter(_ =>
        {
            calls++;
            throw new InvalidOperationException("broken");
        });

        for (var i = 0; i < OpdsImageConverter.MaxConcurrentConversions + 1; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => converter.ConvertAsync([1], "image/webp", OpdsMediaTypes.Jpeg, default));
        }

        Assert.Equal(OpdsImageConverter.MaxConcurrentConversions + 1, calls);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
