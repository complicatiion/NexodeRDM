namespace Nexode.App.Localization;

/// <summary>French and Spanish greetings (generated from the translation files, same order as Greetings.cs).</summary>
public static class GreetingTexts
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> ByLanguage = new Dictionary<string, IReadOnlyDictionary<string, string[]>>
    {
        ["fr"] = new Dictionary<string, string[]>
        {
            ["Morning"] =
            [
                "Bonjour – d'abord le café, ensuite la production.",
                "Bonjour ! Les tâches de nuit ont tourné. Probablement.",
                "Nouveau jour, nouveaux tickets.",
                "Bonjour. D'abord les logs, la panique plus tard.",
                "Debout ! Les sauvegardes, elles, sont déjà levées.",
                "Contrôle du matin : vert, c'est bon.",
            ],
            ["Afternoon"] =
            [
                "Bon après-midi – à mi-chemin de la fenêtre de maintenance.",
                "Bon après-midi. Toujours aucune panne. Pourvu que ça dure.",
                "Des correctifs après le déjeuner ? Audacieux.",
                "Coup de barre ? Les serveurs s'en fichent.",
                "Bon après-midi – la file de tickets vous salue.",
            ],
            ["Evening"] =
            [
                "Bonsoir – l'heure de ce tout dernier redémarrage.",
                "Bonsoir. La fenêtre de maintenance ouvre bientôt.",
                "Encore des heures sup ? Respect.",
                "Un déploiement ce soir ? Courageux.",
                "Bonsoir. N'oubliez pas le plan de retour arrière.",
            ],
            ["Night"] =
            [
                "De garde cette nuit ? Les serveurs apprécient.",
                "Toujours debout ? La supervision aussi.",
                "3 h du matin – l'heure classique des incidents.",
                "Heures creuses. Parfait pour patcher.",
                "Bonne nuit – ou bon incident ?",
                "Il n'y a plus que vous et les tâches cron.",
            ],
            ["Monday"] =
            [
                "Lundi. Voyons ce qui a cassé ce week-end.",
                "Nouvelle semaine, mêmes serveurs.",
                "Lundi – redémarrez votre motivation.",
            ],
            ["Friday"] =
            [
                "C'est vendredi. Vendredi en lecture seule ?",
                "Un déploiement le vendredi ? Réfléchissez-y à deux fois.",
                "Bientôt le week-end – ne touchez pas à la prod.",
                "Vendredi : le jour idéal pour mettre à jour la documentation.",
            ],
            ["Weekend"] =
            [
                "De garde ce week-end ? Vous êtes un héros.",
                "Samedi – mode maintenance activé.",
                "Admin du dimanche. Juste un petit coup d'œil, espérons-le.",
            ],
            ["Neutral"] =
            [
                "Avez-vous essayé d'éteindre et de rallumer ?",
                "C'est toujours le DNS.",
                "Ce n'est pas le DNS. Impossible que ce soit le DNS. C'était le DNS.",
                "Tous les systèmes au vert – pour l'instant.",
                "On n'est jamais mieux que chez 127.0.0.1.",
                "Fenêtre de maintenance ouverte. Bonne chance.",
                "sudo fais-moi un sandwich.",
                "L'uptime, c'est un art de vivre.",
                "Ça marche sur ma machine.",
                "Une sauvegarde ne vaut rien tant qu'on ne l'a pas restaurée.",
                "Un petit ping, peut-être ?",
                "Restez calme et lisez les logs.",
                "Un serveur par jour éloigne les tickets pour toujours.",
                "99,9 % de disponibilité. Les 0,1 %, c'était un vendredi.",
                "Bonjour, root.",
                "Le Patch Tuesday arrive.",
                "Prêt quand vous l'êtes. Pour les connexions, s'entend.",
                "Objectif du jour : zéro surprise.",
                "Le pare-feu dit non. Nexode dit bonjour.",
                "404 : café introuvable.",
                "Erreur 418 : je suis une théière. Toujours connecté.",
                "Chaque panne est une occasion d'apprendre. En général.",
                "Le cloud, c'est juste le serveur de quelqu'un d'autre.",
                "La documentation : l'ultime frontière.",
                "Votre mot de passe expire dans … je plaisante.",
                "Les admins ne dorment pas, ils attendent des timeouts.",
                "SSH entre, les problèmes sortent.",
                "Bon retour, opérateur.",
                "Tableaux de bord au vert, admin heureux.",
                "Que votre latence soit faible.",
            ],
        },
        ["es"] = new Dictionary<string, string[]>
        {
            ["Morning"] =
            [
                "Buenos días: primero el café, luego producción.",
                "¡Buenos días! Las tareas nocturnas se ejecutaron. Probablemente.",
                "Día nuevo, tickets nuevos.",
                "Buenos días. Primero los logs, el pánico después.",
                "¡Arriba! Las copias de seguridad ya madrugaron.",
                "Revisión matutina: verde es bueno.",
            ],
            ["Afternoon"] =
            [
                "Buenas tardes: a mitad de camino de la ventana de cambios.",
                "Buenas tardes. Aún sin caídas. Que siga así.",
                "¿Parches después de comer? Valiente.",
                "¿Bajón de media tarde? A los servidores les da igual.",
                "Buenas tardes: la cola de tickets te saluda.",
            ],
            ["Evening"] =
            [
                "Buenas tardes: hora de ese último reinicio.",
                "Buenas tardes. La ventana de cambios se abre pronto.",
                "¿Horas extra otra vez? Respeto.",
                "¿Despliegue a última hora? Valiente decisión.",
                "Buenas tardes. No olvides el plan de rollback.",
            ],
            ["Night"] =
            [
                "¿Turno de noche? Los servidores te lo agradecen.",
                "¿Sigues despierto? La monitorización también.",
                "Las 3 de la madrugada: la hora clásica de los incidentes.",
                "Horas tranquilas. Perfectas para parchear.",
                "Buenas noches… ¿o buen incidente?",
                "Solo quedáis despiertos tú y los cron jobs.",
            ],
            ["Monday"] =
            [
                "Lunes. A ver qué se rompió el fin de semana.",
                "Semana nueva, mismos servidores.",
                "Lunes: reinicia tu motivación.",
            ],
            ["Friday"] =
            [
                "Es viernes. ¿Viernes de solo lectura?",
                "¿Desplegar en viernes? Piénsalo dos veces.",
                "Casi fin de semana: no toques producción.",
                "Viernes: el mejor día para actualizar la documentación.",
            ],
            ["Weekend"] =
            [
                "¿Turno de fin de semana? Eres un héroe.",
                "Sábado: modo mantenimiento activado.",
                "Admin de domingo. Ojalá solo sea un vistazo.",
            ],
            ["Neutral"] =
            [
                "¿Has probado a apagarlo y volverlo a encender?",
                "Siempre es el DNS.",
                "No es el DNS. Imposible que sea el DNS. Era el DNS.",
                "Todos los sistemas en verde… por ahora.",
                "No hay lugar como 127.0.0.1.",
                "Ventana de cambios abierta. Suerte.",
                "sudo hazme un bocadillo.",
                "El uptime es un estilo de vida.",
                "En mi máquina funciona.",
                "Una copia de seguridad no vale nada hasta que restauras una.",
                "Hazme ping, quizá.",
                "Mantén la calma y revisa los logs.",
                "Un servidor al día, los tickets en la lejanía.",
                "99,9 % de uptime. El 0,1 % fue un viernes.",
                "Hola, root.",
                "Se acerca el Patch Tuesday.",
                "Listo cuando tú lo estés. Las conexiones, digo.",
                "Objetivo de hoy: cero sorpresas.",
                "El firewall dice que no. Nexode te saluda.",
                "404: café no encontrado.",
                "Error 418: soy una tetera. Sigo conectado.",
                "Cada caída es una oportunidad de aprendizaje. Casi siempre.",
                "La nube es solo el servidor de otro.",
                "Documentación: la última frontera.",
                "Tu contraseña caduca en … es broma.",
                "Los admins no duermen, esperan timeouts.",
                "Entra por SSH, saca los problemas.",
                "Bienvenido de nuevo, operador.",
                "Paneles en verde, admin contento.",
                "Que tu latencia sea baja.",
            ],
        },
    };
}
