using JobParser.Services;
using Quartz;

namespace JobParser.Jobs
{
    public class ParserJob : IJob
    {
        private readonly ParserService _parserService;
        private readonly ILogger<ParserJob> _logger;

        public ParserJob(ParserService parserService, ILogger<ParserJob> logger)
        {
            _parserService = parserService;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            _logger.LogInformation("Запуск запланированого парсингу о {Time}", DateTime.Now);

            try
            {
                await _parserService.RunAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка исполнения запланированого парсингу");
            }
        }
    }
}