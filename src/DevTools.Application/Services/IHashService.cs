using DevTools.Shared.Models;

namespace DevTools.Application.Services;

public interface IHashService
{
    HashResult Compute(string input, string algorithm);
    IReadOnlyCollection<string> SupportedAlgorithms { get; }
}
