using System;
using System.Collections.Generic;

namespace ServerManager.WebApplication.Models;

public class ServerManagerApiException : Exception
{
    public ServerManagerApiException() : base()
    { }

    public ServerManagerApiException(int statusCode, ICollection<string> messages) : base()
    {
        StatusCode = statusCode;
        Messages = messages;
    }

    public ServerManagerApiException(int statusCode, ICollection<string> messages, Exception innerException) : base(null, innerException)
    {
        StatusCode = statusCode;
        Messages = messages;
    }

    public int StatusCode { get; private set; } = 0;

    public ICollection<string> Messages { get; private set; } = new List<string>();
}
