using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IdentitySyncPro.Web.Models;
using IdentitySyncPro.Infrastructure.Data;

namespace IdentitySyncPro.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly AppDbContext _db;

    public HomeController(ILogger<HomeController> logger, AppDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    [HttpGet]
    public IActionResult Index()
    {
        return View();
    }

    [HttpGet]
    public IActionResult Privacy()
    {
        return View();
    }

    /// <summary>
    /// UI language toggle — available to every signed-in user regardless of role
    /// (Settings itself is Admin-only, so the layout calls this endpoint instead).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SetLanguage(string lang)
    {
        if (lang != "ar" && lang != "en") return BadRequest();

        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "Language");
        if (setting != null)
        {
            setting.Value = lang;
            setting.ModifiedDate = DateTime.UtcNow;
        }
        else
        {
            _db.AppSettings.Add(new Core.Models.Settings.AppSettings { Key = "Language", Value = lang });
        }
        await _db.SaveChangesAsync();
        return Json(new { success = true });
    }

    /// <summary>
    /// Empty endpoint whose only job is to refresh the sliding auth cookie.
    ///
    /// The layout calls it **only after real user input** (mouse/keyboard/touch), never on a
    /// timer alone — a blind heartbeat would keep an abandoned workstation signed in forever
    /// and defeat the idle-timeout requirement it is meant to accompany. What it does prevent
    /// is the case the timeout would otherwise cause: an operator spending twelve minutes
    /// filling the field-mapping form, pressing Save, and being bounced to the login page with
    /// the work lost.
    ///
    /// GET on purpose: the global antiforgery filter validates non-GET requests only, so this
    /// stays useful even on a page whose token has gone stale.
    /// </summary>
    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult KeepAlive() => NoContent();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [HttpGet]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
