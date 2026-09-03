using System.Text.RegularExpressions;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Checks that no view declares the same section twice.
    ///
    /// Razor compiles a duplicate <c>@section</c> without a word. The build is green, every test
    /// passes, and the page throws <c>InvalidOperationException: Section 'Scripts' is already
    /// defined</c> the first time somebody opens it — a 500 on one screen, discovered by whoever
    /// happened to visit.
    ///
    /// It is easy to write by accident: a view's sections sit at the bottom, and adding a block of
    /// script to a long file naturally means appending one more <c>@section Scripts</c> without
    /// scrolling up to find the one already there.
    ///
    /// This belongs beside the codebase's existing lesson that a green Razor build says nothing
    /// about the JavaScript inside a <c>&lt;script&gt;</c> tag. It says nothing about this either.
    /// </summary>
    public class RazorSectionTests
    {
        private static readonly Regex SectionDeclaration =
            new(@"^\s*@section\s+(?<name>\w+)", RegexOptions.Multiline | RegexOptions.Compiled);

        private static string ViewsRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "IdentitySyncPro.Web", "Views");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Could not locate IdentitySyncPro.Web/Views from the test output folder.");
        }

        [Fact]
        public void NoViewDeclaresTheSameSectionTwice()
        {
            var views = Directory.GetFiles(ViewsRoot(), "*.cshtml", SearchOption.AllDirectories);
            var offenders = new List<string>();

            foreach (var view in views)
            {
                var names = SectionDeclaration.Matches(File.ReadAllText(view))
                    .Select(m => m.Groups["name"].Value)
                    .ToList();

                foreach (var duplicate in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
                {
                    offenders.Add(
                        $"{Path.GetFileName(Path.GetDirectoryName(view))}/{Path.GetFileName(view)} " +
                        $"declares @section {duplicate.Key} {duplicate.Count()} times");
                }
            }

            // A scan that examined nothing would pass just as quietly as the bug it looks for.
            Assert.True(views.Length > 10, $"Only {views.Length} views were scanned — the search path is probably wrong.");
            Assert.Empty(offenders);
        }
    }
}
