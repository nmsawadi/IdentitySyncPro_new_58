namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Converts human-friendly schedule settings (time, interval, days)
    /// into cron expressions for Hangfire scheduling.
    /// </summary>
    public static class CronBuilder
    {
        /// <summary>
        /// Builds a cron expression from user-friendly schedule parameters.
        /// </summary>
        /// <param name="mode">Schedule mode: "interval", "daily", "weekly", "monthly", "custom"</param>
        /// <param name="time">Time in HH:mm format (for daily/weekly/monthly modes)</param>
        /// <param name="days">Comma-separated day numbers 0-6, Sunday=0 (for weekly mode)</param>
        /// <param name="intervalMinutes">Interval in minutes (for interval mode)</param>
        /// <param name="customCron">Custom cron expression (for custom mode)</param>
        /// <param name="dayOfMonth">Day of month 1-28 (for monthly mode)</param>
        /// <returns>A valid cron expression string</returns>
        /// <exception cref="InvalidOperationException">
        /// The custom mode was chosen without a usable expression. It throws rather than falling
        /// back, because the fallback was the bug: the dropdown offered "custom", no field carried
        /// an expression to it, and every service saved that way quietly became a daily one.
        /// </exception>
        public static string Build(string mode, string? time, string? days, int? intervalMinutes,
            string? customCron = null, int? dayOfMonth = null)
        {
            return mode?.ToLower() switch
            {
                "interval" => BuildInterval(intervalMinutes ?? 30),
                "daily" => BuildDaily(time ?? "02:00"),
                "weekly" => BuildWeekly(time ?? "02:00", days ?? "0,1,2,3,4,5,6"),
                "monthly" => BuildMonthly(time ?? "02:00", dayOfMonth ?? 1),
                "custom" => RequireUsableExpression(customCron),
                _ => "0 2 * * *"
            };
        }

        /// <summary>Lowest and highest day a monthly schedule may use. See <see cref="BuildMonthly"/>.</summary>
        public const int MinDayOfMonth = 1;
        public const int MaxDayOfMonth = 28;

        /// <summary>
        /// The custom expression, or a refusal naming what is wrong with it.
        ///
        /// A schedule that is not the schedule you chose has to fail at the moment you choose it,
        /// not at the hour it quietly does the wrong thing.
        /// </summary>
        private static string RequireUsableExpression(string? customCron)
        {
            var problem = Validate(customCron);
            if (problem != null) throw new InvalidOperationException(problem);
            return customCron!.Trim();
        }

        /// <summary>
        /// Checks the shape of a cron expression: five fields, known characters, numbers within the
        /// range their position allows.
        ///
        /// Deliberately structural rather than complete. Hangfire's own parser is the final
        /// authority and is not reachable from here, so this catches the mistakes a person actually
        /// makes — a missing field, a stray word, an hour of 25 — and leaves the exotic remainder to
        /// registration, which reports its own failure instead of swallowing it.
        /// </summary>
        /// <returns>null when the expression is usable, otherwise the reason it is not.</returns>
        public static string? Validate(string? expression)
        {
            var expr = expression?.Trim();
            if (string.IsNullOrEmpty(expr))
                return "No cron expression was entered.";

            var fields = expr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5)
                return $"A cron expression has 5 fields (minute hour day month weekday) — this one has {fields.Length}.";

            var ranges = new[]
            {
                (0, 59, "minute"), (0, 23, "hour"), (1, 31, "day of month"), (1, 12, "month"), (0, 7, "weekday")
            };

            for (var i = 0; i < 5; i++)
            {
                var (min, max, label) = ranges[i];
                if (ValidateField(fields[i], min, max, label) is { } problem) return problem;
            }

            return null;
        }

        private static string? ValidateField(string field, int min, int max, string label)
        {
            // Names (JAN, MON, ...) and markers such as L are handed through to Hangfire's parser,
            // which understands more of them than a range check here safely could.
            if (field.Any(char.IsLetter)) return null;

            foreach (var c in field)
                if (!char.IsDigit(c) && c != '*' && c != ',' && c != '-' && c != '/' && c != '?')
                    return $"'{c}' is not valid in the {label} field ('{field}').";

            foreach (var token in field.Split(',', '-', '/'))
            {
                if (token.Length == 0 || token == "*" || token == "?") continue;
                if (!int.TryParse(token, out var n))
                    return $"'{token}' is not a number in the {label} field ('{field}').";
                if (n < min || n > max)
                    return $"{label} must be between {min} and {max} — '{field}' uses {n}.";
            }

            return null;
        }

        /// <summary>
        /// Converts a cron expression into a human-readable description.
        /// </summary>
        public static string Describe(string cronExpression, bool isArabic = false)
        {
            if (string.IsNullOrEmpty(cronExpression)) return isArabic ? "غير محدد" : "Not set";

            var parts = cronExpression.Split(' ');
            if (parts.Length < 5) return cronExpression;

            // Interval pattern: */N * * * *
            if (parts[0].StartsWith("*/") && parts[1] == "*")
            {
                var minutes = parts[0].Replace("*/", "");
                return isArabic ? $"كل {minutes} دقيقة" : $"Every {minutes} minutes";
            }

            // Hourly interval: 0 */N * * *
            if (parts[0] == "0" && parts[1].StartsWith("*/"))
            {
                var hours = parts[1].Replace("*/", "");
                return isArabic ? $"كل {hours} ساعة" : $"Every {hours} hours";
            }

            // Daily: M H * * *
            if (parts[2] == "*" && parts[3] == "*" && parts[4] == "*")
            {
                var hour = parts[1].PadLeft(2, '0');
                var minute = parts[0].PadLeft(2, '0');
                return isArabic ? $"يومياً الساعة {hour}:{minute}" : $"Daily at {hour}:{minute}";
            }

            // Weekly: M H * * d1,d2
            if (parts[2] == "*" && parts[3] == "*" && parts[4] != "*")
            {
                var hour = parts[1].PadLeft(2, '0');
                var minute = parts[0].PadLeft(2, '0');
                var dayNames = GetDayNames(parts[4], isArabic);
                return isArabic
                    ? $"أسبوعياً ({dayNames}) الساعة {hour}:{minute}"
                    : $"Weekly ({dayNames}) at {hour}:{minute}";
            }

            // Monthly: M H D * *. Without this the schedule list fell through to the raw
            // expression, and a screen printing "0 2 15 * *" has stopped telling the operator
            // anything they did not already have to know.
            if (parts[2] != "*" && parts[3] == "*" && parts[4] == "*")
            {
                var hour = parts[1].PadLeft(2, '0');
                var minute = parts[0].PadLeft(2, '0');
                return isArabic
                    ? $"شهرياً يوم {parts[2]} الساعة {hour}:{minute}"
                    : $"Monthly on day {parts[2]} at {hour}:{minute}";
            }

            return cronExpression;
        }

        private static string BuildInterval(int minutes)
        {
            if (minutes >= 60 && minutes % 60 == 0)
            {
                var hours = minutes / 60;
                return $"0 */{hours} * * *";
            }
            return $"*/{minutes} * * * *";
        }

        private static string BuildDaily(string time)
        {
            var (hour, minute) = ParseTime(time);
            return $"{minute} {hour} * * *";
        }

        private static string BuildWeekly(string time, string days)
        {
            var (hour, minute) = ParseTime(time);
            var cleanDays = string.Join(",", days.Split(',').Select(d => d.Trim()));
            return $"{minute} {hour} * * {cleanDays}";
        }

        /// <summary>
        /// A monthly schedule on a chosen day.
        ///
        /// The day is held to 1-28 on purpose. Cron has no notion of "the last day", so
        /// <c>0 2 31 * *</c> simply does not fire in February, April, June, September or November —
        /// a schedule that skips five months a year while looking perfectly configured. A month-end
        /// run that is genuinely wanted belongs in the custom mode, written out explicitly.
        /// </summary>
        private static string BuildMonthly(string time, int dayOfMonth)
        {
            var (hour, minute) = ParseTime(time);
            var day = Math.Clamp(dayOfMonth, MinDayOfMonth, MaxDayOfMonth);
            return $"{minute} {hour} {day} * *";
        }

        private static (int hour, int minute) ParseTime(string time)
        {
            var parts = time.Split(':');
            var hour = parts.Length > 0 && int.TryParse(parts[0], out var h) ? h : 2;
            var minute = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
            return (hour, minute);
        }

        private static string GetDayNames(string days, bool isArabic)
        {
            var arNames = new[] { "الأحد", "الإثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت" };
            var enNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

            var names = isArabic ? arNames : enNames;
            var dayList = days.Split(',')
                .Select(d => int.TryParse(d.Trim(), out var idx) && idx >= 0 && idx <= 6 ? names[idx] : d.Trim());

            return string.Join(", ", dayList);
        }
    }
}
