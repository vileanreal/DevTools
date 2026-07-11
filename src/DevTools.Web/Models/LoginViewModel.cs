using System.ComponentModel.DataAnnotations;

namespace DevTools.Web.Models;

public sealed class LoginViewModel
{
    [Required]
    [StringLength(200)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(500)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}
