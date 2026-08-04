using System.IO;
using Kata.Core.Sessions;

namespace Kata.Tests;

public class SessionHandshakeTests
{
    [Fact]
    public void Publish_ThenTryRead_RoundTripsSolutionPath()
    {
        var slnPath = Path.GetTempFileName();
        try
        {
            SessionHandshake.Publish(slnPath);

            var payload = SessionHandshake.TryRead();

            Assert.NotNull(payload);
            Assert.Equal(Path.GetFullPath(slnPath), payload!.SolutionPath);
            Assert.Equal(System.Environment.ProcessId, payload.PublisherProcessId);
        }
        finally
        {
            SessionHandshake.Clear();
            File.Delete(slnPath);
        }
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenReferencedSolutionMissing()
    {
        var slnPath = Path.GetTempFileName();
        SessionHandshake.Publish(slnPath);
        File.Delete(slnPath);
        try
        {
            var payload = SessionHandshake.TryRead();
            Assert.Null(payload);
        }
        finally
        {
            SessionHandshake.Clear();
        }
    }

    [Fact]
    public void Clear_RemovesFile()
    {
        var slnPath = Path.GetTempFileName();
        try
        {
            SessionHandshake.Publish(slnPath);
            Assert.True(File.Exists(SessionHandshake.HandshakeFilePath));
            SessionHandshake.Clear();
            Assert.False(File.Exists(SessionHandshake.HandshakeFilePath));
        }
        finally
        {
            File.Delete(slnPath);
        }
    }
}
