using ADFi.Infrastructure.Security;

namespace ADFi.Core.Tests;

public sealed class PasswordCredentialHasherTests
{
    [Fact]
    public void HashIsSaltedAndVerifiesInConstantTimePrimitive()
    {
        var first = PasswordCredentialHasher.Hash("Correct horse 7!");
        var second = PasswordCredentialHasher.Hash("Correct horse 7!");

        Assert.NotEqual(first, second);
        Assert.True(PasswordCredentialHasher.Verify("Correct horse 7!", first));
        Assert.False(PasswordCredentialHasher.Verify("Wrong horse 7!", first));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("alllowercase123!")]
    [InlineData("ALLUPPERCASE123!")]
    [InlineData("NoDigitsHere!")]
    [InlineData("NoSymbolsHere123")]
    public void WeakPasswordsAreRejected(string password)
    {
        Assert.Throws<ArgumentException>(() => PasswordCredentialHasher.ValidatePassword(password));
    }
}
