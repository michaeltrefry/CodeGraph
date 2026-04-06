using TC.Common.Configuration.Application;
using TC.Common.TcServiceStack.ServiceRegistry.Checks;

namespace TC.CodeGraphJobs.Controllers;

public class HealthController(IApplicationInstance application) : HealthCheckController(application);
