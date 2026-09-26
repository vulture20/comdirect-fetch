using System.Text;
using ComdirectFetch.Worker;

namespace ComdirectFetch.Tests;

public class AdminAuthTests
{
    private static string BasicHeader(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Fact]
    public void Akzeptiert_korrektes_Passwort_unabhaengig_vom_Benutzernamen()
    {
        Assert.True(AdminAuth.TryValidate(BasicHeader("admin", "geheim123"), "geheim123"));
        Assert.True(AdminAuth.TryValidate(BasicHeader("egal-welcher-name", "geheim123"), "geheim123"));
    }

    [Fact]
    public void Lehnt_falsches_Passwort_ab()
    {
        Assert.False(AdminAuth.TryValidate(BasicHeader("admin", "falsch"), "geheim123"));
    }

    [Fact]
    public void Lehnt_fehlenden_Header_ab()
    {
        Assert.False(AdminAuth.TryValidate(null, "geheim123"));
        Assert.False(AdminAuth.TryValidate("", "geheim123"));
    }

    [Fact]
    public void Lehnt_Header_ohne_Basic_Praefix_ab()
    {
        Assert.False(AdminAuth.TryValidate("Bearer irgendwas", "geheim123"));
    }

    [Fact]
    public void Lehnt_ungueltiges_Base64_ab()
    {
        Assert.False(AdminAuth.TryValidate("Basic !!!nicht-base64!!!", "geheim123"));
    }

    [Fact]
    public void Lehnt_Header_ohne_Doppelpunkt_ab()
    {
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("keinDoppelpunkt"));
        Assert.False(AdminAuth.TryValidate(header, "geheim123"));
    }
}
