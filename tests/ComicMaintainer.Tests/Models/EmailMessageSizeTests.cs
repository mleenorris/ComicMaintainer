using ComicMaintainer.Core.Models;
using MimeKit;
using MimeKit.IO;

namespace ComicMaintainer.Tests.Models;

/// <summary>
/// The email size limit is enforced on the encoded message, which is about a
/// third larger than the attachment.
/// </summary>
public class EmailMessageSizeTests
{
    private const long Megabyte = 1024 * 1024;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(57)]
    [InlineData(1000)]
    [InlineData(300_001)]
    [InlineData(2 * Megabyte + 17)]
    public void EstimateMessageBytes_IsAnUpperBoundOfTheRealEncodedEmail(long attachmentBytes)
    {
        var actual = MeasureEmail(attachmentBytes);
        var estimate = EmailMessageSize.EstimateMessageBytes(attachmentBytes);

        Assert.True(estimate >= actual, $"Estimated {estimate} bytes but the email is {actual}.");
        Assert.True(estimate - actual <= EmailMessageSize.MessageOverheadBytes, "The estimate stays close to the real size.");
    }

    [Fact]
    public void EstimateMessageBytes_AccountsForBase64AndLineBreaks()
    {
        // 3 bytes become 4 characters; 57 bytes fill one 76 character line plus CRLF.
        Assert.Equal(78 + EmailMessageSize.MessageOverheadBytes, EmailMessageSize.EstimateMessageBytes(57));
        Assert.Equal(80 + 4 + EmailMessageSize.MessageOverheadBytes, EmailMessageSize.EstimateMessageBytes(58));

        // A 25 MB file is a 34 MB email.
        var encoded = EmailMessageSize.EstimateMessageBytes(25 * Megabyte);
        Assert.InRange(encoded / (double)Megabyte, 34.0, 35.0);
    }

    [Theory]
    [InlineData(1 * Megabyte)]
    [InlineData(10 * Megabyte)]
    [InlineData(25 * Megabyte)]
    [InlineData(35 * Megabyte)]
    [InlineData(200 * Megabyte)]
    public void MaxAttachmentBytes_IsTheLargestAttachmentThatFits(long limit)
    {
        var max = EmailMessageSize.MaxAttachmentBytes(limit);

        Assert.True(EmailMessageSize.EstimateMessageBytes(max) <= limit);
        Assert.True(EmailMessageSize.EstimateMessageBytes(max + 1) > limit);
        Assert.True(MeasureEmail(Math.Min(max, 2 * Megabyte)) <= limit);
    }

    [Fact]
    public void MaxAttachmentBytes_LeavesAbout18MegabytesOfA25MegabyteLimit()
    {
        var max = EmailMessageSize.MaxAttachmentBytes(25 * Megabyte);

        Assert.InRange(max / (double)Megabyte, 18.0, 18.5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(EmailMessageSize.MessageOverheadBytes)]
    public void MaxAttachmentBytes_IsZeroWhenNothingFits(long limit)
    {
        Assert.Equal(0, EmailMessageSize.MaxAttachmentBytes(limit));
    }

    /// <summary>Size of the email the sender builds for an attachment of this size.</summary>
    private static long MeasureEmail(long attachmentBytes)
    {
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress("ComicMaintainer", "comics@example.com"));
        message.To.Add(MailboxAddress.Parse("someone_long_name@kindle.com"));
        message.Subject = "A Rather Long Series Name Volume 12 001-050";

        var body = new BodyBuilder { TextBody = "A Rather Long Series Name Volume 12 001-050 sent from ComicMaintainer." };
        var content = new MemoryStream(new byte[attachmentBytes]);
        body.Attachments.Add(new MimePart("application", "epub+zip")
        {
            Content = new MimeContent(content),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = "A Rather Long Series Name Volume 12 001-050.epub"
        });
        message.Body = body.ToMessageBody();

        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        using var measure = new MeasuringStream();
        message.WriteTo(options, measure);
        return measure.Length;
    }
}
