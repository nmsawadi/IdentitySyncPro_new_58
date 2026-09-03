using System.Text.RegularExpressions;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Holds the service form and the action that saves it to the same list of fields.
    ///
    /// The Edit action copies the entity property by property. A field the form posts and the action
    /// never mentions is accepted by the browser, saved by nothing, and reported to nobody: the
    /// operator changes a setting, the page says it saved, and the stored value is the old one. It
    /// is invisible until somebody notices the behaviour did not change — which for a governance
    /// setting could be a whole attestation cycle later.
    ///
    /// <b>Parsed from source, not hand-listed</b>, on the same reasoning as
    /// <see cref="NullableColumnAgreementTests"/>: a hand-written list is a third declaration to keep
    /// in step with the other two, and would be the next thing to fall out of date.
    /// </summary>
    public class PostedFieldsAreSavedTests
    {
        private static string Find(params string[] relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new InvalidOperationException($"Could not locate {string.Join('/', relative)} from the test output folder.");
        }

        [Fact]
        public void EveryFieldTheServiceFormPostsIsCopiedByTheEditAction()
        {
            var view = File.ReadAllText(Find("IdentitySyncPro.Web", "Views", "Services", "Edit.cshtml"));
            var controller = File.ReadAllText(Find("IdentitySyncPro.Web", "Controllers", "ServicesController.cs"));
            var model = File.ReadAllText(Find("IdentitySyncPro.Core", "Models", "Services", "SvcService.cs"));

            var properties = Regex.Matches(model, @"public\s+[\w?<>\[\]]+\s+(\w+)\s*\{\s*get")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            var posted = Regex.Matches(view, @"name=""(\w+)""")
                .Select(m => m.Groups[1].Value)
                .Where(properties.Contains)
                .ToHashSet(StringComparer.Ordinal);

            var body = controller.Split("public async Task<IActionResult> Edit(int id, SvcService model)")[1]
                                 .Split("public async Task<IActionResult>")[0];

            var assigned = Regex.Matches(body, @"service\.(\w+)\s*=")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            var dropped = posted.Except(assigned).OrderBy(x => x, StringComparer.Ordinal).ToList();

            // A comparison that examined nothing passes as quietly as the bug it looks for.
            Assert.True(posted.Count > 40, $"Only {posted.Count} posted fields were found — the view path or the pattern is wrong.");

            Assert.True(dropped.Count == 0,
                "These fields are posted by Services/Edit.cshtml and never copied by the Edit action, " +
                "so changing them on screen does nothing and says nothing: " + string.Join(", ", dropped));
        }
    }
}
