using IdentitySyncPro.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IdentitySyncPro.Web.Filters
{
    /// <summary>
    /// Action filter that automatically sets ViewBag.Lang from the database
    /// for every controller action, so all views have access to the current language.
    /// </summary>
    public class LanguageFilter : IActionFilter
    {
        private readonly AppDbContext _db;

        public LanguageFilter(AppDbContext db)
        {
            _db = db;
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.Controller is Controller controller)
            {
                try
                {
                    var langSetting = _db.AppSettings.FirstOrDefault(s => s.Key == "Language");
                    controller.ViewBag.Lang = langSetting?.Value ?? "ar";
                }
                catch
                {
                    controller.ViewBag.Lang = "ar";
                }
            }
        }

        public void OnActionExecuted(ActionExecutedContext context) { }
    }
}
