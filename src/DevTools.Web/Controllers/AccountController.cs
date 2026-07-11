using System.Security.Claims;
using DevTools.Web.Models;
using DevTools.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DevTools.Web.Controllers;

[Route("account")]
public sealed class AccountController(IOptions<LoginOptions> options, ILoginCredentialVerifier verifier) : Controller
{
    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (!options.Value.UseLogin) return RedirectToAction("Index", "Home");
        if (User.Identity?.IsAuthenticated == true) return LocalRedirectOrHome(returnUrl);
        ViewData["Title"] = "Login";
        return View(new LoginViewModel { ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null });
    }

    [AllowAnonymous]
    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!options.Value.UseLogin) return RedirectToAction("Index", "Home");
        model.ReturnUrl = Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl : null;
        if (!ModelState.IsValid)
        {
            ViewData["Title"] = "Login";
            return View(model);
        }

        if (!verifier.IsValid(model.Username, model.Password))
        {
            ModelState.AddModelError(string.Empty, "The username or password is incorrect.");
            ViewData["Title"] = "Login";
            return View(model);
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, model.Username.Trim())],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = model.RememberMe,
                AllowRefresh = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(model.RememberMe ? 24 : 8)
            });
        return LocalRedirectOrHome(model.ReturnUrl);
    }

    [Authorize]
    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    private IActionResult LocalRedirectOrHome(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Home")!;
}
