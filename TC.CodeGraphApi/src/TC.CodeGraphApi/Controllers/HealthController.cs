using TC.Common.Configuration.Application;
using TC.Common.TcServiceStack.ServiceRegistry.Checks;

namespace TC.CodeGraphApi.Controllers;

public class HealthController(IApplicationInstance application) : HealthCheckController(application);