using System.Security.Claims;
using IdentitySyncPro.Web.Controllers;
using IdentitySyncPro.Web.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the boundary between the employee portal and the console.
    ///
    /// The portal is reachable by anyone in the directory — 118,000 people rather than a handful of
    /// operators — so the question "can a portal principal reach an administrative screen?" has to
    /// have a provable answer.
    ///
    /// There are two barriers, and these tests cover both separately on purpose. Barrier one is the
    /// absence of a role claim, which every <c>[Authorize(Roles = ...)]</c> screen enforces without
    /// knowing the portal exists. Barrier two is this filter, which closes the screens that ask only
    /// for an authenticated user. A test that only exercised the filter would pass just as happily
    /// on a build where the portal principal had been given a role by mistake.
    /// </summary>
    public class PortalGuardTests
    {
        // ══════════════════════════════════════
        // BARRIER ONE — NO ROLE, EVER
        // ══════════════════════════════════════

        /// <summary>
        /// The claims the portal issues, mirrored from <see cref="PortalController"/>. Kept here as
        /// data so the assertion below is about the shape of the principal rather than about
        /// reaching into a controller action.
        /// </summary>
        private static ClaimsPrincipal PortalPrincipal(string account = "440000001") =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, account),
                new Claim(PortalController.PortalClaim, "1")
            }, "TestScheme"));

        private static ClaimsPrincipal ConsolePrincipal(string user = "isp-admin", string role = "Admin") =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, user),
                new Claim(ClaimTypes.Role, role)
            }, "TestScheme"));

        /// <summary>
        /// The safety net. Every administrative screen in the application is gated on a role, so a
        /// principal carrying none is refused by all of them even if this filter were deleted.
        /// </summary>
        [Theory]
        [InlineData("Admin")]
        [InlineData("Operator")]
        [InlineData("Viewer")]
        public void APortalPrincipal_HoldsNoConsoleRole(string role)
        {
            Assert.False(PortalPrincipal().IsInRole(role));
        }

        [Fact]
        public void APortalPrincipal_CarriesNoRoleClaimAtAll()
        {
            Assert.Empty(PortalPrincipal().FindAll(ClaimTypes.Role));
        }

        [Fact]
        public void APortalPrincipal_IsStillIdentifiable()
        {
            // The account name is what every request is filed under, so it has to survive.
            Assert.Equal("440000001", PortalPrincipal().Identity!.Name);
        }

        // ══════════════════════════════════════
        // BARRIER TWO — THE FILTER
        // ══════════════════════════════════════

        private static ActionExecutingContext Context(ClaimsPrincipal? user, string controller, string action = "Index")
        {
            var http = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) };
            var routeData = new RouteData();
            routeData.Values["controller"] = controller;
            routeData.Values["action"] = action;

            var actionContext = new ActionContext(http, routeData, new ControllerActionDescriptor());
            return new ActionExecutingContext(
                actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: null!);
        }

        private static string? RedirectedTo(ActionExecutingContext context) =>
            (context.Result as RedirectToActionResult)?.ControllerName;

        /// <summary>
        /// The screens barrier one cannot see: those asking only for an authenticated user.
        /// <c>AccessRequests</c> is exactly one, and deliberately — approving is not a console role
        /// — which makes it the screen a portal principal would otherwise walk straight into.
        /// </summary>
        [Theory]
        [InlineData("AccessRequests")]
        [InlineData("AccessCatalog")]
        [InlineData("Dashboard")]
        [InlineData("Settings")]
        [InlineData("Users")]
        public void APortalUser_IsTurnedAwayFromConsoleScreens(string controller)
        {
            var context = Context(PortalPrincipal(), controller);

            new PortalGuardFilter().OnActionExecuting(context);

            Assert.Equal("Portal", RedirectedTo(context));
        }

        [Theory]
        [InlineData("Index")]
        [InlineData("Request")]
        [InlineData("Cancel")]
        public void APortalUser_MovesFreelyInsideThePortal(string action)
        {
            var context = Context(PortalPrincipal(), "Portal", action);

            new PortalGuardFilter().OnActionExecuting(context);

            Assert.Null(context.Result);
        }

        /// <summary>
        /// A console user on the portal files requests whose subject is their console username —
        /// frequently not a directory account at all. Those would be raised for an account that
        /// does not exist and fail at the first membership check.
        /// </summary>
        [Fact]
        public void AConsoleUser_IsSentBackToTheConsole()
        {
            var context = Context(ConsolePrincipal(), "Portal");

            new PortalGuardFilter().OnActionExecuting(context);

            Assert.Equal("Dashboard", RedirectedTo(context));
        }

        /// <summary>Leaving must always work. Bouncing somebody away from the sign-out button is a small trap of its own.</summary>
        [Fact]
        public void SignOut_IsReachableFromEitherSide()
        {
            var console = Context(ConsolePrincipal(), "Portal", nameof(PortalController.Logout));
            new PortalGuardFilter().OnActionExecuting(console);
            Assert.Null(console.Result);
        }

        [Fact]
        public void AConsoleUser_IsLeftAloneOnConsoleScreens()
        {
            var context = Context(ConsolePrincipal(), "Dashboard");

            new PortalGuardFilter().OnActionExecuting(context);

            Assert.Null(context.Result);
        }

        /// <summary>
        /// Anonymous traffic is not this filter's business — the sign-in pages and the authorization
        /// policy handle it. Redirecting here would put the portal's own login page behind a
        /// redirect to itself.
        /// </summary>
        [Theory]
        [InlineData("Portal")]
        [InlineData("Account")]
        [InlineData("Sspr")]
        public void AnAnonymousVisitor_IsNotRedirectedByThisFilter(string controller)
        {
            var context = Context(user: null, controller);

            new PortalGuardFilter().OnActionExecuting(context);

            Assert.Null(context.Result);
        }

        /// <summary>
        /// Matching must not depend on how the route capitalises a name, since that varies with how
        /// a link was written rather than with who is asking.
        /// </summary>
        [Theory]
        [InlineData("portal")]
        [InlineData("PORTAL")]
        public void ThePortalIsRecognisedWhateverTheCasing(string controller)
        {
            var context = Context(PortalPrincipal(), controller, "Index");

            new PortalGuardFilter().OnActionExecuting(context);

            Assert.Null(context.Result);
        }
    }
}
