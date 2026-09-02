namespace SmartFinance.Application.Exceptions;

public class BadRequestException : Exception
{
    public BadRequestException(string message) : base(message)
    {}
}

public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message)
    {}
}

public class UnauthorizedException : Exception
{
    public UnauthorizedException(string message) : base(message)
    {}
}

public class ExternalServiceException : Exception
{
    public ExternalServiceException(string message) : base(message)
    {}
}
/// <summary>
/// Islem premium abonelik gerektiriyor.
///
/// Ayri bir tip: istemcinin bunu diger 400'lerden ayirt edip hata mesaji
/// yerine paywall ekrani acabilmesi gerekiyor. HTTP 402 (Payment Required)
/// tam olarak bu durum icin ayrilmis bir kod.
/// </summary>
public class PremiumRequiredException : Exception
{
    public PremiumRequiredException(string message) : base(message)
    {}
}
