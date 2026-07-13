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