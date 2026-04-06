namespace OrderManagement;

public static class ApiErrors
{
    public static IResult JsonError(int status, string message, string code = "ERROR") =>
        Results.Json(new { error = message, code }, AppJson.Options, statusCode: status);
}
