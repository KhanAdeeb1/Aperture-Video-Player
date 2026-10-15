using Aperture.Core.Server;

namespace Aperture.Core.Tests.Server;

public class PassphraseTests
{
    // Few iterations keep the tests fast; the stored value records its own count.
    private const int FastIterations = 1000;

    [Fact]
    public void A_hash_verifies_only_its_own_passphrase()
    {
        var stored = Passphrase.Hash("correct horse battery", FastIterations);

        Assert.True(Passphrase.Verify("correct horse battery", stored));
        Assert.False(Passphrase.Verify("correct horse batterY", stored));
        Assert.False(Passphrase.Verify(string.Empty, stored));
        Assert.False(Passphrase.Verify(null, stored));
        Assert.DoesNotContain("correct", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_passphrase_hashes_differently_each_time()
    {
        Assert.NotEqual(Passphrase.Hash("correct horse battery", FastIterations), Passphrase.Hash("correct horse battery", FastIterations));
    }

    [Fact]
    public void New_hashes_use_the_full_iteration_count()
    {
        Assert.StartsWith("pbkdf2-sha256$600000$", Passphrase.Hash("correct horse battery"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("md5$1$abc$def")]
    [InlineData("pbkdf2-sha256$0$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$1000$not base64!$AAAA")]
    public void Malformed_stored_values_never_match(string? stored) =>
        Assert.False(Passphrase.Verify("anything at all", stored));

    [Fact]
    public void Overlong_passphrases_are_refused_before_hashing()
    {
        var stored = Passphrase.Hash("correct horse battery", FastIterations);
        Assert.False(Passphrase.Verify(new string('a', Passphrase.MaximumLength + 1), stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("         x")]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("abababababab")]
    public void Weak_passphrases_are_explained(string? passphrase) =>
        Assert.NotNull(Passphrase.Check(passphrase));

    [Fact]
    public void Ordinary_passphrases_are_accepted() =>
        Assert.Null(Passphrase.Check("lantern river 42"));

    [Fact]
    public void Device_tokens_are_random_and_stored_only_as_hashes()
    {
        var a = DeviceToken.Create();
        var b = DeviceToken.Create();

        Assert.NotEqual(a, b);
        Assert.True(DeviceToken.LooksValid(a));
        Assert.Equal(64, DeviceToken.HashOf(a).Length);
        Assert.Equal(DeviceToken.HashOf(a), DeviceToken.HashOf(a));
        Assert.DoesNotContain(a, DeviceToken.HashOf(a), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("../../../../etc/passwd/aaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa+")]
    public void Malformed_tokens_are_rejected_before_any_lookup(string? token) =>
        Assert.False(DeviceToken.LooksValid(token));
}
