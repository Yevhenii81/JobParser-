JobParser

Описание

Проект состоит из двух частей:

PhoneNumberApi (API проверки телефонов)

Хранит телефоны в PostgreSQL и отвечает на запросы проверки номера.

Логика: если номер новый — сохраняет его и возвращает статус Created (201). Если номер уже есть — возвращает Conflict (409).

JobParser (парсер вакансий)

Собирает объявления с сайтов, вытягивает контакты (телефоны, email), фильтрует по списку исключений, проверяет телефоны через PhoneNumberApi и сохраняет валидные лиды в CSV.

Для AmountWork используется Selenium, потому что сайт блокирует обычные HTTP-запросы.

Источники

AmountWork: https://amountwork.com/ua/rabota/ssha/voditel (в приоритете, работает через Selenium)

Layboard: https://layboard.com/ua/vakansii/ssha (работает через HttpClient)

Как работает фильтрация и дедупликация

Из объявления извлекаются все телефоны и email

Номера нормализуются в формат E164 (например, +17794278752)

Каждый номер проверяется через PhoneNumberApi:

201 Created: номера не было, он записан в базу, объявление считается новым (сохраняется в CSV)

409 Conflict: номер уже был в базе, объявление отклоняется как дубликат

Дополнительно используется таблица processed_leads в базе JobParser, чтобы не парсить одни и те же URL повторно между запусками
Применяется фильтр исключений из файла exclusions.txt (отсеивает объявления со словами-исключениями)

Прогресс по страницам

AmountWork:

Запуск 1: Страницы 1-10 (100 объявлений)

Запуск 2: Страницы 11-20 (100 объявлений)

Запуск 3: Страницы 21-30 (100 объявлений)

При достижении конца списка начинает сначала

Layboard:

Запуск 1: Категории 0-4 (50 объявлений)

Запуск 2: Категории 5-9 (50 объявлений)

Запуск 3: Категории 10-14 (50 объявлений)

При достижении конца категорий начинает сначала

Прогресс сохраняется в таблице parser_progress и восстанавливается после перезапуска приложения.

Требования

.NET SDK 8.0

PostgreSQL 16

Google Chrome (для Selenium парсинга AmountWork)

Базы данных

Рекомендуется держать разные базы для проектов:

PhoneNumberApi:

Database: phone_db

Table: phone_numbers

JobParser:

Database: jobparser_db

Tables:

phone_numbers (локальное хранилище, опционально)

processed_leads (список обработанных URL)

parser_progress (прогресс сканирования страниц)

Структура базы данных JobParser

Таблица phone_numbers:

Id (автоинкремент)

Number (уникальный телефон)

CreatedAt (дата добавления)

Таблица processed_leads:

Id (автоинкремент)

Url (уникальный URL объявления)

Source (имя сайта: AmountWork/Layboard)

ProcessedAt (дата обработки)

Таблица parser_progress:

Id (автоинкремент)

Source (имя сайта)

LastProcessedPage (последняя обработанная страница/категория)

UpdatedAt (дата обновления)

Структура проекта

text

JobParser/

├── Data/

│   └── AppDbContext.cs               (контекст Entity Framework)

├── Helpers/

│   ├── ExclusionFilter.cs            (фильтр по словам-исключениям)

│   └── PhoneExtractor.cs             (извлечение телефонов из текста)

├── Jobs/

│   └── ParserJob.cs                  (фоновая задача Quartz для автозапуска)

├── Migrations/

│   ├── 20260409203615_InitialCreate.cs

│   ├── 20260411110415_AddProcessedLeadsTable.cs

│   ├── 20260411175047_AddParserProgress.cs

│   └── AppDbContextModelSnapshot.cs  (миграции базы данных)

├── Models/

│   ├── JobLead.cs                    (модель лида с контактами)

│   ├── PhoneRecord.cs                (модель телефона)

│   ├── ProcessedLead.cs              (модель обработанного URL)

│   ├── ParserProgress.cs             (модель прогресса парсинга)

│   └── ParserSettings.cs             (модель настроек парсера)

├── Services/

│   ├── Interfaces/

│   │   └── ISiteParser.cs            (интерфейс парсера)

│   ├── AmountWorkParser.cs           (парсер AmountWork через Selenium)

│   ├── LayboardParser.cs             (парсер Layboard через HttpClient)

│   ├── ParserService.cs              (координатор парсеров)

│   ├── PhoneCheckerService.cs        (проверка телефонов через API)

│   └── CsvExportService.cs           (экспорт результатов в CSV)

├── logs/                             (папка с лог-файлами)

│   └── parser-20260411.log           (файл логов за день)

├── out/                              (папка с CSV результатами)

│   └── [2026.04.11_21.02]_leads.csv  (объединенный CSV с обоих сайтов)

├── appsettings.json                  (конфигурация приложения)

├── exclusions.txt                    (список слов-исключений)

├── Program.cs                        (точка входа)

└── README.md

Настройка конфигов

PhoneNumberApi appsettings.json

JSON

{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=phone_db;Username=postgres;Password={ТВОЙ_ПАРОЛЬ}"
  }

}

JobParser appsettings.json

JSON

{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=jobparser_db;Username=postgres;Password={ТВОЙ_ПАРОЛЬ}"
  },
  "ParserSettings": {
    "ExclusionsFilePath": "exclusions.txt",
    "OutputFolder": "out",
    "MaxPagesToScan": 10,
    "DelayBetweenRequests": 1500,
    "MaxLeadsPerSite": 100,
    "CategoriesPerRun": 5,
    "UseExternalPhoneApi": true,
    "PhoneCheckApiUrl": "https://localhost:7249/PhoneNumber/check_number",
    "UseSeleniumForAmountWork": true
},

  "Quartz": {
    "CronSchedule": "0 0 */6 * * ?"
  }
}

Параметры:

MaxPagesToScan — количество страниц за один запуск (10)

DelayBetweenRequests — задержка между запросами в миллисекундах (1500)

MaxLeadsPerSite — максимум лидов с одного сайта (100, 0 = без лимита)

CategoriesPerRun — количество категорий Layboard за запуск (5)

UseSeleniumForAmountWork — использовать Selenium для AmountWork (true)

UseExternalPhoneApi — проверять телефоны через API (true)

PhoneCheckApiUrl — URL API проверки телефонов

Запуск и проверка

Шаг 1. Поднять PostgreSQL

Создайте базы phone_db и jobparser_db (или используйте существующие).

SQL

CREATE DATABASE phone_db;

CREATE DATABASE jobparser_db;

Шаг 2. Применить миграции

В проекте JobParser выполните:

Bash

dotnet ef migrations add InitialCreate

dotnet ef database update

Шаг 3. Создать файл exclusions.txt

В корне проекта создайте файл со словами-исключениями (по одному на строку):

text

# Слова-исключения (каждое с новой строки)

# Строки, начинающиеся с # — комментарии

scam

мошенничество

обман

fraud

spam

Шаг 4. Запустить PhoneNumberApi

Запустите API проверки телефонов. Убедитесь, что он доступен по адресу из PhoneCheckApiUrl.

Шаг 5. Запустить JobParser

Bash

dotnet run

Откройте Swagger (http://localhost:5000/swagger) и выполните:

text

POST /api/parser/run

Результат

В папке out появится CSV файл с результатами обоих сайтов:

text

out/

└── [2026.04.11_21.02]_leads.csv

Формат CSV:

text

Title,Phones,Email,Location,Source,ParsedAt

Водитель CDL,+1234567890,test@example.com,Чикаго,AmountWork,2025-01-15T20:45:00Z

Водитель дальнобой,+1987654321,driver@mail.com,Майами,Layboard,2025-01-15T20:46:00Z

В логах будет статистика:

text

logs/

└── parser-20260411.log

Содержимое лог-файла:

text

20:45:37 [INF] Database ready. Phones: 0, Processed URLs: 395

20:45:47 [INF] Запуск парсинга

20:45:47 [INF] Парсер: AmountWork

20:45:50 [INF] Начало парсинга AmountWork. Страницы 1-10

20:46:15 [INF] Найдено ссылок: 98, новых: 12

20:48:23 [INF] [1] Водитель CDL | Phones: 2 | Email: test@mail.com | Loc: Чикаго

20:50:45 [INF] Завершено AmountWork. Лидов: 12

Для каждого дня создается отдельный лог-файл с датой в названии.

Эндпоинты JobParser
text

GET  /                  - Информация о сервисе

POST /api/parser/run    - Ручной запуск парсинга

GET  /api/stats         - Статистика по телефонам

GET  /swagger           - Swagger UI документация

Планировщик (Quartz)

Quartz уже подключен. Расписание задается в appsettings.json:

JSON

"Quartz": {
  "CronSchedule": "0 0 */6 * * ?"
}

По умолчанию запуск каждые 6 часов.

Примеры расписаний:

Каждый час: 0 0 * * * ?

Каждые 12 часов: 0 0 */12 * * ?

Каждый день в 9:00: 0 0 9 * * ?

Файл exclusions.txt

Содержит слова/фразы, при наличии которых в Title или Description объявление исключается.

Пример:

text

# Слова-исключения (каждое с новой строки)

# Строки, начинающиеся с # — комментарии

scam

мошенничество

обман

fraud

spam

Типовые проблемы

AmountWork возвращает 500 при HttpClient

Это нормально: сайт блокирует обычные запросы. Используйте Selenium (UseSeleniumForAmountWork=true).

Selenium работает медленно

Это ожидаемо. Ускорение достигается уменьшением DelayBetweenRequests, но слишком маленькая задержка может привести к блокировкам.

Второй запуск ничего не находит

Это ожидаемо, если:

Телефоны уже записаны в phone_db (PhoneNumberApi), поэтому все объявления становятся дубликатами

URL уже записаны в processed_leads (JobParser), поэтому страницы повторно не обрабатываются

Парсер автоматически продолжит со следующих страниц благодаря таблице parser_progress.

Таблица parser_progress не существует

Выполните миграцию:

Bash

dotnet ef migrations add AddParserProgress

dotnet ef database update

Логи не выводятся в консоль

Проверьте что в appsettings.json установлен правильный уровень логирования:

JSON

"Serilog": {
  "MinimumLevel": {
    "Default": "Information"
  }
}

Логирование

Логи сохраняются в папку logs с ежедневным созданием нового файла:

text

logs/

├── parser-20260409.log

├── parser-20260410.log

├── parser-20260411.log

└── parser-20260412.log

Формат лога:

text

20:45:37 [INF] Database ready. Phones: 1523, Processed URLs: 3456

20:45:47 [INF] Запуск парсинга

20:45:47 [INF] Парсер: AmountWork

20:45:50 [INF] Начало парсинга AmountWork. Страницы 1-10
