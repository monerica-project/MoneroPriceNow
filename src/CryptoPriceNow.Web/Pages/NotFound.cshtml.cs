using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Web.Pages;

public class NotFoundModel : PageModel
{
    public void OnGet() => this.Response.StatusCode = 404;
}
