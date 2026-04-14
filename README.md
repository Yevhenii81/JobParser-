JobParser

Описание

Проект состоит из двух частей:

1. PhoneNumberApi (API проверки телефонов)

Хранит телефоны в PostgreSQL

Отвечает на запросы проверки номера

Логика:

Если номер новый -> сохраняет и возвращает 201 Created

Если номер уже есть -> возвращает 409 Conflict

2. JobParser (парсер вакансий)

Собирает объявления с сайтов

Извлекает контакты (телефоны, email)

Фильтрует по списку исключений

Проверяет телефоны через PhoneNumberApi

Сохраняет валидные лиды в CSV

Для AmountWork используется Selenium, так как сайт блокирует обычные HTTP-запросы

Источники

AmountWork: https://amountwork.com/ua/rabota/ssha/voditel(приоритетный, работает через Selenium)

Layboard: https://layboard.com/ua/vakansii/ssha(работает через HttpClient)

Фильтрация и дедупликация

Из объявления извлекаются телефоны и email

Телефоны очищаются от мусора (даты, зарплаты и т.д.)

Пример: 20012026, 46004900 — удаляются

Проверка через PhoneNumberApi:

201 Created -> новый номер -> сохраняется

409 Conflict -> дубликат -> отбрасывается

Дополнительно:

Таблица processed_leads — чтобы не парсить одни и те же URL

Файл exclusions.txt — фильтрация по словам

Прогресс парсинга

AmountWork

Запуск 1: страницы 1–10

Запуск 2: страницы 11–20

Запуск 3: страницы 21–30

После конца списка → начинается заново

Layboard

Запуск 1: категории 0–4

Запуск 2: категории 5–9

Запуск 3: категории 10–14

После конца -> начинается заново

Прогресс хранится в таблице parser_progress.

Требования

.NET 8

PostgreSQL 16

Google Chrome (для Selenium)

Базы данных

PhoneNumberApi

Database: phone_db

Table: phone_numbers

JobParser

Database: jobparser_db

Tables:

processed_leads

parser_progress

Структура базы JobParser

processed_leads

Id

Url (уникальный)

Source

ProcessedAt

parser_progress

Id

Source

LastProcessedPage

UpdatedAt

Структура проекта

JobParser/

├── Data/

│   └── AppDbContext.cs

├── Helpers/

│   ├── ExclusionFilter.cs

│   └── ParserHelper.cs

├── Jobs/

│   └── ParserJob.cs

├── Migrations/

├── Models/

│   ├── JobLead.cs

│   ├── ProcessedLead.cs

│   ├── ParserProgress.cs

│   └── ParserSettings.cs

├── Repositories/

│   ├── ProgressRepository.cs

│   └── ProcessedUrlsRepository.cs

├── Services/

│   ├── Interfaces/

│   │   └── ISiteParser.cs

│   ├── AmountWorkParser.cs

│   ├── LayboardParser.cs

│   ├── ParserService.cs

│   ├── PhoneCheckerService.cs

│   ├── HtmlParserService.cs

│   └── CsvExportService.cs

├── logs/

├── out/

├── appsettings.json

├── exclusions.txt

├── Program.cs

└── README.md

Конфигурация

PhoneNumberApi
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=phone_db;Username=postgres;Password=YOUR_PASSWORD"
  }
}

JobParser
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=jobparser_db;Username=postgres;Password=YOUR_PASSWORD"
  },
  "ParserSettings": {
    "ExclusionsFilePath": "exclusions.txt",
    "OutputFolder": "out",
    "MaxPagesToScan": 10,
    "DelayBetweenRequests": 1000,
    "MaxLeadsPerSite": 100,
    "CategoriesPerRun": 5,
    "UseExternalPhoneApi": true,
    "PhoneCheckApiUrl": "http://localhost:7249/PhoneNumber/check_number",
    "UseSeleniumForAmountWork": true
  },
  "Quartz": {
    "CronSchedule": "0 0 */6 * * ?"
  }
}

Запуск

1. Создать базы

CREATE DATABASE phone_db;

CREATE DATABASE jobparser_db;

2. Миграции

dotnet ef migrations add InitialCreate

dotnet ef database update

3. exclusions.txt

scam

мошенничество

обман

fraud

spam

4. Запуск API

Запусти PhoneNumberApi

5. Запуск парсера

dotnet run

Swagger:

http://localhost:5000/swagger

Результат

CSV
out/
└── 2026.01.15_21.02_leads.csv

Title,Description,PhoneNumbers,Email,Location,Source,Region,ParsedAt

Логи
logs/
└── parser-YYYYMMDD.log

API эндпоинты

GET  /                  - информация

POST /api/parser/run    - запуск

GET  /api/stats         - статистика

GET  /swagger           - документация

Планировщик (Quartz)

"Quartz": {
  "CronSchedule": "0 0 */6 * * ?"
}

Примеры:

Каждый час -> 0 0 * * * ?

Каждые 12 часов -> 0 0 */12 * * ?

Каждый день в 9 -> 0 0 9 * * ?

Типовые проблемы

AmountWork 500 ошибка

Это нормально → нужен Selenium

Ничего не парсится

Причины:

номера уже есть в phone_db

URL уже есть в processed_leads

Нет parser_progress

dotnet ef migrations add AddParserProgress

dotnet ef database update

Нет логов

Проверь уровень:

"Serilog": {
  "MinimumLevel": {
    "Default": "Information"
  }
}

Архитектура

ParserHelper — утилиты

ProgressRepository — прогресс

ProcessedUrlsRepository — дедупликация

HtmlParserService — парсинг HTML

PhoneCheckerService — проверка телефонов

AmountWork / Layboard — парсеры

Валидация телефонов

Валидация выполняется только в PhoneNumberApi.

JobParser только извлекает и отправляет номера.
