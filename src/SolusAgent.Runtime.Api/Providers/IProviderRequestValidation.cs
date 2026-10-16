namespace SolusAgent.Runtime.Api.Providers;

/// <summary>Pure bounded admission of an actual complete prospective request, including provider-owned wire limits.</summary>
/// <remarks>Never dispatch, observe/seal an attempt, import state, access credentials or invoke Host effects here. Required by correction-capable providers; this is not a transport planner or authority grant.</remarks>
public interface IProviderRequestValidation
{
    /// <summary>Returns None only when the current actual projection fits; failure is fixed and contains no payload.</summary>
    ProviderError ValidateRequest(ProviderRequest request);
}
