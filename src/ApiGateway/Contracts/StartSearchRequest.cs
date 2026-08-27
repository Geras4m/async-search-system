namespace ApiGateway.Contracts;

/// <summary>
/// Body of a <c>POST /searches</c> request: the criteria a new hotel search is started with.
/// </summary>
/// <param name="Destination">
/// Destination to look for hotels in, for example <c>"Paris"</c>. Required; its length must be
/// greater than two and less than one hundred characters.
/// </param>
public sealed record StartSearchRequest(string Destination);
