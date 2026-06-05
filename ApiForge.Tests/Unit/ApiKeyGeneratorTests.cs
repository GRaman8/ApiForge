using System.Security.Cryptography;
using System.Text;
using ApiForge.Api.Features.Keys;

namespace ApiForge.Tests.Unit;

public class ApiKeyGeneratorTests
{
    [Fact]
    public void Generate_returns_plaintext_with_expected_prefix()
    {
        var (plaintext, _, prefix) = ApiKeyGenerator.Generate();

        Assert.Equal("apf_live_", prefix);
        Assert.StartsWith("apf_live_", plaintext);
    }

    [Fact]
    public void Generate_hash_is_sha256_of_plaintext()
    {
        var (plaintext, hash, _) = ApiKeyGenerator.Generate();

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void Generate_produces_unique_keys()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ApiKeyGenerator.Generate().plaintext).ToHashSet();
        Assert.Equal(100, keys.Count);
    }

    [Fact]
    public void Generate_does_not_contain_url_unsafe_chars()
    {
        var (plaintext, _, _) = ApiKeyGenerator.Generate();
        Assert.DoesNotContain('+', plaintext);
        Assert.DoesNotContain('/', plaintext);
        Assert.DoesNotContain('=', plaintext);
    }
}
