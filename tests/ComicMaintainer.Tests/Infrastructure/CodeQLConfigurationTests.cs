using System.Text.RegularExpressions;

namespace ComicMaintainer.Tests.Infrastructure;

public class CodeQLConfigurationTests
{
    [Fact]
    public void Workflows_DefaultSetup_DoesNotIncludeAdvancedCodeQLAnalysis()
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && current != null; i++)
        {
            var workflows = Path.Combine(current, ".github", "workflows");
            if (Directory.Exists(workflows))
            {
                var files = Directory.EnumerateFiles(workflows)
                    .Where(path => Path.GetExtension(path) is ".yml" or ".yaml")
                    .ToList();

                Assert.NotEmpty(files);
                foreach (var file in files)
                {
                    // Non-CodeQL tools may still use codeql-action/upload-sarif.
                    Assert.False(
                        Regex.IsMatch(File.ReadAllText(file),
                            @"(?im)^\s*-?\s*uses:\s*['""]?github/codeql-action/(init|analyze)@"),
                        $"Default setup conflicts with advanced CodeQL analysis in {file}.");
                }
                return;
            }
            current = Path.GetDirectoryName(current);
        }
        throw new DirectoryNotFoundException("Could not locate workflows relative to test binaries.");
    }
}
