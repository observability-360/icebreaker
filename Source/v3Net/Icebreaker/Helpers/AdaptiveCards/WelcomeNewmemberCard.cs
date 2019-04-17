﻿//----------------------------------------------------------------------------------------------
// <copyright file="WelcomeNewMemberCard.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>
//----------------------------------------------------------------------------------------------

namespace Icebreaker.Helpers.AdaptiveCards
{
    using System.Collections.Generic;
    using System.IO;
    using System.Web.Hosting;
    using Icebreaker.Properties;

    /// <summary>
    /// Builder class for the welcome new member card
    /// </summary>
    public static class WelcomeNewMemberCard
    {
        /// <summary>
        /// Creates the welcome new member card.
        /// </summary>
        /// <param name="teamName">The team name</param>
        /// <param name="personFirstName">The first name of the new member</param>
        /// <param name="botDisplayName">The bot name</param>
        /// <param name="botInstaller">The person that installed the bot to the team</param>
        /// <returns>The welcome new member card</returns>
        public static string GetCard(string teamName, string personFirstName, string botDisplayName, string botInstaller)
        {
            string introductoryMessage = string.Empty;

            if (string.IsNullOrEmpty(botInstaller))
            {
                introductoryMessage = string.Format(Resources.InstallMessageUnknownInstaller, teamName);
            }
            else
            {
                introductoryMessage = string.Format(Resources.InstallMessageKnownInstaller, botInstaller, teamName);
            }

            var variablesToValues = new Dictionary<string, string>()
            {
                { "team", teamName },
                { "personFirstName", personFirstName },
                { "botDisplayName", botDisplayName },
                { "introMessage", introductoryMessage }
            };

            var cardJsonFilePath = HostingEnvironment.MapPath("~/Helpers/AdaptiveCards/WelcomeNewMemberCard.json");
            var cardTemplate = File.ReadAllText(cardJsonFilePath);

            var cardBody = cardTemplate;

            foreach (var kvp in variablesToValues)
            {
                cardBody = cardBody.Replace($"%{kvp.Key}%", kvp.Value);
            }

            return cardBody;
        }
    }
}