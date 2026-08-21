using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using YARPUI.Resources;

namespace YARPUI.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private const string UsernameSetting = "YarpUi:Auth:Username";
    private const string PasswordSetting = "YarpUi:Auth:Password";

    private readonly IConfiguration _configuration;
    private readonly ILogger<LoginModel> _logger;
    private readonly IStringLocalizer<UIStrings> _localizer;

    public LoginModel(
        IConfiguration configuration,
        ILogger<LoginModel> logger,
        IStringLocalizer<UIStrings> localizer)
    {
        _configuration = configuration;
        _logger = logger;
        _localizer = localizer;
    }

    [BindProperty]
    public string? Username { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var expectedUsername = _configuration[UsernameSetting];
        var expectedPassword = _configuration[PasswordSetting];

        if (string.IsNullOrEmpty(expectedUsername) || string.IsNullOrEmpty(expectedPassword))
        {
            _logger.LogWarning("Login attempted but {UsernameSetting}/{PasswordSetting} are not configured.", UsernameSetting, PasswordSetting);
            ErrorMessage = _localizer["login.notConfigured"];
            return Page();
        }

        if (!FixedTimeEquals(Username, expectedUsername) || !FixedTimeEquals(Password, expectedPassword))
        {
            _logger.LogWarning("Failed sign-in attempt for user '{Username}'.", Username);
            ErrorMessage = _localizer["login.invalidCredentials"];
            return Page();
        }

        var identity = new ClaimsIdentity(YarpUiDefaults.Scheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, Username!));

        await HttpContext.SignInAsync(
            YarpUiDefaults.Scheme,
            new ClaimsPrincipal(identity));

        _logger.LogInformation("User '{Username}' signed in.", Username);

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Index");
    }

    private static bool FixedTimeEquals(string? actual, string expected)
    {
        var actualBytes = Encoding.UTF8.GetBytes(actual ?? string.Empty);
        var expectedBytes = Encoding.UTF8.GetBytes(expected ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
