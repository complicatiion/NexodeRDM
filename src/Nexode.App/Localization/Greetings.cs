namespace Nexode.App.Localization;

/// <summary>
/// Start page greetings for admins. Time of day and weekday take priority, otherwise a neutral or humorous line.
/// Add lines freely: every entry is (English, German); French and Spanish come from GreetingTexts (same order).
/// </summary>
public static partial class Greetings
{
    private static readonly (string En, string De)[] Morning =
    [
        ("Good morning – coffee first, then production.", "Guten Morgen – erst Kaffee, dann Produktion."),
        ("Morning! The overnight jobs ran. Probably.", "Moin! Die Nacht-Jobs sind gelaufen. Vermutlich."),
        ("Fresh day, fresh tickets.", "Neuer Tag, neue Tickets."),
        ("Good morning. Logs first, panic later.", "Guten Morgen. Erst Logs lesen, dann Panik."),
        ("Rise and shine – the backups already did.", "Aufstehen – die Backups sind schon fertig."),
        ("Morning check: green is good.", "Morgenrunde: Grün ist gut.")
    ];

    private static readonly (string En, string De)[] Afternoon =
    [
        ("Good afternoon – halfway to the change window.", "Guten Tag – Halbzeit bis zum Wartungsfenster."),
        ("Afternoon. Still no outage. Keep it that way.", "Nachmittag. Noch kein Ausfall. Bleibt so."),
        ("Post-lunch patching? Bold.", "Patchen nach dem Mittag? Mutig."),
        ("Afternoon slump? The servers don't care.", "Mittagstief? Den Servern egal."),
        ("Good afternoon – the ticket queue says hi.", "Guten Tag – die Ticket-Queue grüßt.")
    ];

    private static readonly (string En, string De)[] Evening =
    [
        ("Good evening – time for that one last reboot.", "Guten Abend – Zeit für den einen letzten Neustart."),
        ("Evening. The change window opens soon.", "Abend. Das Wartungsfenster öffnet gleich."),
        ("Overtime again? Respect.", "Schon wieder Überstunden? Respekt."),
        ("Evening deploy? Brave choice.", "Deployment am Abend? Mutige Wahl."),
        ("Good evening. Don't forget the rollback plan.", "Guten Abend. Rollback-Plan nicht vergessen.")
    ];

    private static readonly (string En, string De)[] Night =
    [
        ("Night shift? The servers appreciate it.", "Nachtschicht? Die Server danken es dir."),
        ("Still awake? So is the monitoring.", "Noch wach? Das Monitoring auch."),
        ("3 a.m. – the classic time for incidents.", "3 Uhr nachts – die klassische Incident-Zeit."),
        ("Quiet hours. Perfect for patching.", "Ruhige Stunden. Perfekt zum Patchen."),
        ("Good night – or good incident?", "Gute Nacht – oder guter Incident?"),
        ("Only you and the cron jobs are up.", "Nur du und die Cronjobs sind wach.")
    ];

    private static readonly (string En, string De)[] Monday =
    [
        ("Monday. Let's see what broke over the weekend.", "Montag. Mal sehen, was am Wochenende kaputtging."),
        ("New week, same servers.", "Neue Woche, gleiche Server."),
        ("Monday – reboot your motivation.", "Montag – Motivation neu starten.")
    ];

    private static readonly (string En, string De)[] Friday =
    [
        ("It's Friday. Read-only Friday?", "Freitag. Heute nur lesen, nicht deployen?"),
        ("Friday deploy? Think twice.", "Deployment am Freitag? Lieber zweimal überlegen."),
        ("Almost weekend – don't touch production.", "Fast Wochenende – Finger weg von Produktion."),
        ("Friday: the best day to update documentation.", "Freitag: der perfekte Tag für Doku.")
    ];

    private static readonly (string En, string De)[] Weekend =
    [
        ("Weekend shift? You're a hero.", "Wochenenddienst? Du bist ein Held."),
        ("Saturday – maintenance mode activated.", "Samstag – Wartungsmodus aktiv."),
        ("Sunday admin. Hopefully just checking.", "Sonntags-Admin. Hoffentlich nur ein kurzer Blick.")
    ];

    private static readonly (string En, string De)[] Neutral =
    [
        ("Have you tried turning it off and on again?", "Schon mal aus- und wieder eingeschaltet?"),
        ("It's always DNS.", "Es ist immer DNS."),
        ("It's not DNS. There's no way it's DNS. It was DNS.", "Es ist nicht DNS. Kann nicht DNS sein. Es war DNS."),
        ("All systems green – for now.", "Alles grün – vorerst."),
        ("There's no place like 127.0.0.1.", "Kein Ort wie 127.0.0.1."),
        ("Change window open. Good luck.", "Wartungsfenster offen. Viel Glück."),
        ("Sudo make me a sandwich.", "Sudo mach mir ein Sandwich."),
        ("Uptime is a lifestyle.", "Uptime ist eine Lebenseinstellung."),
        ("Works on my machine.", "Bei mir läuft's."),
        ("Backups are worth nothing until you restore one.", "Ein Backup ist erst etwas wert, wenn der Restore klappt."),
        ("Ping me maybe.", "Ping mich doch mal."),
        ("Keep calm and check the logs.", "Ruhig bleiben und Logs lesen."),
        ("A server a day keeps the tickets away.", "Ein Server am Tag hält die Tickets fern."),
        ("99.9 % uptime. The 0.1 % was a Friday.", "99,9 % Uptime. Die 0,1 % waren ein Freitag."),
        ("Hello, root.", "Hallo, root."),
        ("Patch Tuesday is coming.", "Patchday naht."),
        ("Ready when you are. Connections, that is.", "Bereit, wenn du es bist. Die Verbindungen jedenfalls."),
        ("Today's goal: zero surprises.", "Ziel für heute: null Überraschungen."),
        ("Firewall says no. Nexode says hi.", "Die Firewall sagt nein. Nexode sagt hallo."),
        ("404: Coffee not found.", "404: Kaffee nicht gefunden."),
        ("Error 418: I'm a teapot. Still connected.", "Fehler 418: Ich bin eine Teekanne. Trotzdem verbunden."),
        ("Every outage is a learning opportunity. Mostly.", "Jeder Ausfall ist eine Lernchance. Meistens."),
        ("The cloud is just someone else's server.", "Die Cloud ist nur der Server von jemand anderem."),
        ("Documentation: the final frontier.", "Dokumentation: unendliche Weiten."),
        ("Your password expires in … just kidding.", "Dein Passwort läuft ab in … kleiner Scherz."),
        ("Admins don't sleep, they wait for timeouts.", "Admins schlafen nicht, sie warten auf Timeouts."),
        ("SSH in, problems out.", "Rein per SSH, raus mit den Problemen."),
        ("Welcome back, operator.", "Willkommen zurück, Operator."),
        ("Green dashboards, happy admin.", "Grüne Dashboards, glücklicher Admin."),
        ("May your latency be low.", "Möge die Latenz niedrig sein.")
    ];

    /// <summary>Plain greeting by time of day (greetings switched off).</summary>
    public static string Plain(DateTime now) => Loc.T(now.Hour switch
    {
        >= 5 and < 11 => "Start.Morning",
        >= 11 and < 18 => "Start.Afternoon",
        >= 18 and < 23 => "Start.Evening",
        _ => "Start.Night"
    });

    /// <summary>Random line: 50 % from the time/day group, otherwise neutral; the last 10 lines are not repeated.</summary>
    public static string Pick(DateTime now, IList<string> recent, Random? random = null)
    {
        random ??= Random.Shared;
        var special = now.DayOfWeek switch
        {
            DayOfWeek.Saturday or DayOfWeek.Sunday => Weekend,
            DayOfWeek.Monday when now.Hour < 12 => Monday,
            DayOfWeek.Friday when now.Hour >= 12 => Friday,
            _ => null
        };
        var timeGroup = now.Hour switch
        {
            >= 5 and < 11 => Morning,
            >= 11 and < 18 => Afternoon,
            >= 18 and < 23 => Evening,
            _ => Night
        };

        var roll = random.NextDouble();
        var group = special is not null && roll < 0.35 ? special : roll < 0.55 ? timeGroup : Neutral;
        var indexes = Enumerable.Range(0, group.Length).ToList();
        var fresh = indexes.Where(i => !recent.Contains(group[i].En)).ToList();
        if (fresh.Count == 0) fresh = indexes;
        var index = fresh[random.Next(fresh.Count)];
        var pick = group[index];
        recent.Add(pick.En);
        while (recent.Count > 10) recent.RemoveAt(0);
        return Text(group, index, pick);
    }

    private static string Text((string En, string De)[] group, int index, (string En, string De) pick)
    {
        if (Loc.Language == "de") return pick.De;
        if (Loc.Language is "fr" or "es" && GreetingTexts.ByLanguage.TryGetValue(Loc.Language, out var groups)
            && groups.TryGetValue(NameOf(group), out var lines) && index < lines.Length) return lines[index];
        return pick.En;
    }

    private static string NameOf((string En, string De)[] group) =>
        group == Morning ? "Morning" : group == Afternoon ? "Afternoon" : group == Evening ? "Evening" : group == Night ? "Night"
        : group == Monday ? "Monday" : group == Friday ? "Friday" : group == Weekend ? "Weekend" : "Neutral";

    public static int Count => Morning.Length + Afternoon.Length + Evening.Length + Night.Length + Monday.Length + Friday.Length + Weekend.Length + Neutral.Length;
}
