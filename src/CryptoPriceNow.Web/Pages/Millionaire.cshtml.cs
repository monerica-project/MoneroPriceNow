using CryptoPriceNow.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Pages;

/// <summary>/millionaire: how much XMR makes you a USDT millionaire, now and 1/7/30/90 days ago.</summary>
public sealed class MillionaireModel : PageModel
{
    private readonly MillionaireService _millionaire;

    public MillionaireModel(MillionaireService millionaire) => _millionaire = millionaire;

    public MillionaireService.Snapshot Data { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct) => Data = await _millionaire.GetAsync(ct);
}
