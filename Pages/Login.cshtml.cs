using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyFlare.Pages;

public sealed class LoginModel(AdminPassword credentials) : PageModel
{
    public bool Changed { get; private set; }
    public bool InvalidPassword { get; private set; }
    public void OnGet(bool changed = false) => Changed = changed;

    public async Task<IActionResult> OnPostAsync(string? password)
    {
        if (credentials.Verify(password) is not { } stamp)
        {
            InvalidPassword = true;
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Page();
        }
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, stamp)], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(new ClaimsPrincipal(identity));
        return RedirectToPage("/Admin");
    }
}
