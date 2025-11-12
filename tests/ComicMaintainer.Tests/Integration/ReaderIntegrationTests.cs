using System.Net;

namespace ComicMaintainer.Tests.Integration;

/// <summary>
/// Integration tests for the Comic Reader functionality.
/// These tests verify that the reader loads correctly and doesn't open new windows.
/// Note: These are file-based tests that verify JavaScript content directly.
/// </summary>
public class ReaderIntegrationTests
{
    private readonly string _wwwrootPath;

    public ReaderIntegrationTests()
    {
        // Find the wwwroot directory from the test project
        var currentDir = Directory.GetCurrentDirectory();
        var solutionDir = Directory.GetParent(currentDir)?.Parent?.Parent?.Parent?.Parent?.FullName;
        _wwwrootPath = Path.Combine(solutionDir ?? "", "src", "ComicMaintainer.WebApi", "wwwroot");
        
        if (!Directory.Exists(_wwwrootPath))
        {
            throw new DirectoryNotFoundException($"wwwroot directory not found at {_wwwrootPath}");
        }
    }

    [Fact]
    public void ReaderHtml_FileExists()
    {
        // Arrange
        var readerPath = Path.Combine(_wwwrootPath, "reader.html");

        // Act & Assert
        Assert.True(File.Exists(readerPath), $"reader.html should exist at {readerPath}");
    }

    [Fact]
    public void ReaderHtml_ContainsExpectedElements()
    {
        // Arrange
        var readerPath = Path.Combine(_wwwrootPath, "reader.html");
        var content = File.ReadAllText(readerPath);

        // Assert
        Assert.Contains("Comic Reader", content);
        Assert.Contains("reader-container", content);
        Assert.Contains("reader-content", content);
        Assert.Contains("id=\"readerContent\"", content);
    }

    [Fact]
    public void ReaderHtml_ContainsReadingModeToggle()
    {
        // Arrange
        var readerPath = Path.Combine(_wwwrootPath, "reader.html");
        var content = File.ReadAllText(readerPath);

        // Assert - Verify reading mode toggle exists
        Assert.Contains("toggleReadingMode", content);
        Assert.Contains("id=\"modeToggleBtn\"", content);
        Assert.Contains("Mode: Manga", content);
    }

    [Fact]
    public void ReaderHtml_ContainsWebcomicModeSupport()
    {
        // Arrange
        var readerPath = Path.Combine(_wwwrootPath, "reader.html");
        var content = File.ReadAllText(readerPath);

        // Assert - Verify webcomic mode elements exist
        Assert.Contains("webcomic-mode", content);
        Assert.Contains("loadWebcomicMode", content);
        Assert.Contains("webcomicContainer", content);
    }

    [Fact]
    public void ReaderHtml_ContainsContinuousScrollLogic()
    {
        // Arrange
        var readerPath = Path.Combine(_wwwrootPath, "reader.html");
        var content = File.ReadAllText(readerPath);

        // Assert - Verify continuous scroll functions exist
        Assert.Contains("handleWebcomicScroll", content);
        Assert.Contains("loadNextComic", content);
        Assert.Contains("prefetchNextComic", content);
    }

    [Fact]
    public void ReaderHtml_ContainsMKeyShortcut()
    {
        // Arrange
        var readerPath = Path.Combine(_wwwrootPath, "reader.html");
        var content = File.ReadAllText(readerPath);

        // Assert - Verify M key toggle is documented and implemented
        Assert.Contains("Toggle Reading Mode", content);
        Assert.Contains("<kbd>M</kbd>", content);
        Assert.Contains("case 'm':", content);
        Assert.Contains("case 'M':", content);
    }

    [Fact]
    public void MainJs_DoesNotContainWindowOpen()
    {
        // Arrange
        var mainJsPath = Path.Combine(_wwwrootPath, "js", "main.js");
        var content = File.ReadAllText(mainJsPath);

        // Assert - Verify readComic function exists and uses window.location.href, not window.open
        Assert.Contains("function readComic(filepath)", content);
        
        // Extract the readComic function
        var readComicStart = content.IndexOf("function readComic(filepath)");
        Assert.True(readComicStart >= 0, "readComic function should exist");
        
        var readComicEnd = content.IndexOf("}", readComicStart);
        Assert.True(readComicEnd > readComicStart, "readComic function should have closing brace");
        
        var readComicFunction = content.Substring(readComicStart, readComicEnd - readComicStart + 1);
        
        // Verify it uses window.location.href and not window.open with _blank
        Assert.Contains("window.location.href", readComicFunction);
        Assert.DoesNotContain("window.open", readComicFunction);
        Assert.DoesNotContain("_blank", readComicFunction);
    }

    [Fact]
    public void MainJs_ReadComicFunction_NavigatesInSameWindow()
    {
        // Arrange
        var mainJsPath = Path.Combine(_wwwrootPath, "js", "main.js");
        var content = File.ReadAllText(mainJsPath);

        // Assert - Extract and verify the readComic function implementation
        var readComicStart = content.IndexOf("function readComic(filepath)");
        var readComicEnd = content.IndexOf("}", readComicStart);
        var readComicFunction = content.Substring(readComicStart, readComicEnd - readComicStart + 1);

        // The function should navigate to reader.html with the file parameter
        Assert.Contains("/reader.html?file=", readComicFunction);
        Assert.Contains("encodeURIComponent(filepath)", readComicFunction);
        
        // Should use window.location.href for same-window navigation
        Assert.Contains("window.location.href =", readComicFunction);
    }

    [Fact]
    public void MainJs_ContainsReadComicAction()
    {
        // Arrange
        var mainJsPath = Path.Combine(_wwwrootPath, "js", "main.js");
        var content = File.ReadAllText(mainJsPath);

        // Assert - Verify the Read Comic action is available in the file actions dropdown
        Assert.Contains("Read Comic", content);
        Assert.Contains("readComic", content);
        Assert.Contains("📖 Read Comic", content);
    }

    [Fact]
    public void ReaderHtml_ContainsNavigationControls()
    {
        // Arrange
        var readerPath = Path.Combine(_wwwrootPath, "reader.html");
        var content = File.ReadAllText(readerPath);

        // Assert
        Assert.Contains("prevBtn", content);
        Assert.Contains("nextBtn", content);
        Assert.Contains("pageSelect", content);
        Assert.Contains("← Previous", content);
        Assert.Contains("Next →", content);
    }

    [Fact]
    public void ReaderHtml_HasBackButton()
    {
        // Arrange
        var readerPath = Path.Combine(_wwwrootPath, "reader.html");
        var content = File.ReadAllText(readerPath);

        // Assert - Verify there's a back button to return to the main page
        Assert.Contains("goBack()", content);
        Assert.Contains("← Back", content);
    }

    [Fact]
    public void ReaderHtml_ContainsKeyboardShortcuts()
    {
        // Arrange
        var readerPath = Path.Combine(_wwwrootPath, "reader.html");
        var content = File.ReadAllText(readerPath);

        // Assert
        Assert.Contains("shortcuts-help", content);
        Assert.Contains("Keyboard Shortcuts", content);
        Assert.Contains("Previous Page", content);
        Assert.Contains("Next Page", content);
    }
}
