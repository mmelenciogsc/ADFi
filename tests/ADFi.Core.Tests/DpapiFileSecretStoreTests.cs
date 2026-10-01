using System.Text;
using ADFi.Infrastructure.Security;

namespace ADFi.Core.Tests;

public sealed class DpapiFileSecretStoreTests
{
    [Fact]
    public async Task RoundTripEncryptsAtRestForCurrentWindowsUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"adfi-secret-{Guid.NewGuid():N}");
        try
        {
            var store = new DpapiFileSecretStore(root, "ADFi desktop OAuth tokens v1");
            var clear = Encoding.UTF8.GetBytes("refresh-token-value");
            await store.SaveAsync("youtube:user-1", clear, TestContext.Current.CancellationToken);

            var file = Directory.GetFiles(root, "*.secret").Single();
            var bytesAtRest = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("refresh-token-value", Encoding.UTF8.GetString(bytesAtRest), StringComparison.Ordinal);
            Assert.Equal(clear, await store.LoadAsync("youtube:user-1", TestContext.Current.CancellationToken));

            await store.DeleteAsync("youtube:user-1", TestContext.Current.CancellationToken);
            Assert.Null(await store.LoadAsync("youtube:user-1", TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
