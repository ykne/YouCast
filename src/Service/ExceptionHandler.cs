using System;

namespace Service
{
    public static class ExceptionHandler
    {
        public static void Handle(Exception exception) =>
            ServiceLog.Error("YouCast", exception.ToString());
    }
}
