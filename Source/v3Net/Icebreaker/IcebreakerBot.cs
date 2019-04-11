﻿//----------------------------------------------------------------------------------------------
// <copyright file="IcebreakerBot.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>
//----------------------------------------------------------------------------------------------

namespace Icebreaker
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Helpers;
    using Helpers.AdaptiveCards;
    using Microsoft.ApplicationInsights;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.Azure;
    using Microsoft.Bot.Connector;
    using Microsoft.Bot.Connector.Teams;
    using Microsoft.Bot.Connector.Teams.Models;
    using Newtonsoft.Json;

    public static class IcebreakerBot
    {
        private static TelemetryClient telemetry = new TelemetryClient(new TelemetryConfiguration(CloudConfigurationManager.GetSetting("AppInsightsInstrumentationKey")));

        public static async Task<int> MakePairsAndNotify()
        {
            telemetry.TrackTrace("Hit the MakePairsAndNotify method at: " + DateTime.Now.ToString());

            // Recall all the teams where we have been added
            // For each team where I have been added:
            //     Pull the roster of each team where I have been added
            //     Remove the members who have opted out of pairs
            //     Match each member with someone else
            //     Save this pair
            // Now notify each pair found in 1:1 and ask them to reach out to the other person
            // When contacting the user in 1:1, give them the button to opt-out.

            // Get teams to which the app has been installed
            var teams = IcebreakerBotDataProvider.GetInstalledTeams();

            var countPairsNotified = 0;
            var maxPairUpsPerTeam = Convert.ToInt32(CloudConfigurationManager.GetSetting("MaxPairUpsPerTeam"));

            telemetry.TrackTrace($"Retrieved {teams.Count} teams at: " + DateTime.Now.ToString());

            telemetry.TrackTrace($"{maxPairUpsPerTeam} pairs maximum");

            foreach (var team in teams)
            {
                try
                {
                    var optedInUsers = await GetOptedInUsers(team);
                    var connectorClient = new ConnectorClient(new Uri(team.ServiceUrl));
                    var teamName = await GetTeamNameAsync(team.ServiceUrl, team.TeamId, connectorClient);

                    telemetry.TrackTrace($"Trying to pair members of {teamName} at: " + DateTime.Now.ToString());

                    foreach (var pair in MakePairs(optedInUsers).Take(maxPairUpsPerTeam))
                    {
                        await NotifyPair(team.ServiceUrl, team.TenantId, teamName, pair);

                        countPairsNotified++;
                    }
                }
                catch (UnauthorizedAccessException uae)
                {
                    telemetry.TrackException(uae);
                }
            }

            telemetry.TrackTrace($"{countPairsNotified} pairs notified at: " + DateTime.Now.ToString());

            return countPairsNotified;
        }

        public static async Task WelcomeUser(string serviceUrl, string memberAddedId, string tenantId, string teamId)
        {
            var connectorClient = new ConnectorClient(new Uri(serviceUrl));

            var teamName = await GetTeamNameAsync(serviceUrl, teamId, connectorClient);

            var allMembers = await GetTeamMembers(serviceUrl, teamId, tenantId, connectorClient);

            var botDisplayName = CloudConfigurationManager.GetSetting("BotDisplayName");

            TeamsChannelAccount userThatJustJoined = null;

            foreach (var m in allMembers)
            {
                // both values are 29: values
                if (m.Id == memberAddedId)
                {
                    userThatJustJoined = m;
                }
            }

            if (userThatJustJoined != null)
            {
                telemetry.TrackTrace($"A new user just joined - {userThatJustJoined.ObjectId}, {userThatJustJoined.GivenName}");
                var welcomeMessageCard = WelcomeNewMemberCard.GetCard(teamName, userThatJustJoined.Name, botDisplayName);
                await NotifyUser(serviceUrl, welcomeMessageCard, userThatJustJoined, tenantId, connectorClient);
            }
        }

        public static async Task SaveAddedToTeam(string serviceUrl, string teamId, string tenantId)
        {
            await IcebreakerBotDataProvider.SaveTeamInstallStatus(new TeamInstallInfo() { ServiceUrl = serviceUrl, TeamId = teamId, TenantId = tenantId }, true);
        }

        public static async Task SaveRemoveFromTeam(string serviceUrl, string teamId, string tenantId)
        {
            await IcebreakerBotDataProvider.SaveTeamInstallStatus(new TeamInstallInfo() { ServiceUrl = serviceUrl, TeamId = teamId, TenantId = tenantId }, false);
        }

        public static async Task OptOutUser(string tenantId, string userId, string serviceUrl)
        {
            await IcebreakerBotDataProvider.SetUserOptInStatus(tenantId, userId, false, serviceUrl);
        }

        public static async Task OptInUser(string tenantId, string userId, string serviceUrl)
        {
            await IcebreakerBotDataProvider.SetUserOptInStatus(tenantId, userId, true, serviceUrl);
        }

        private static async Task<string> GetTeamNameAsync(string serviceUrl, string teamId, ConnectorClient client)
        {
            telemetry.TrackTrace("Getting the team name now at: " + DateTime.Now.ToString());

            var teamsConnectorClient = client.GetTeamsConnectorClient();
            var teamDetailsResult = await teamsConnectorClient.Teams.FetchTeamDetailsAsync(teamId);
            return teamDetailsResult.Name;
        }

        private static async Task NotifyPair(string serviceUrl, string tenantId, string teamName, Tuple<ChannelAccount, ChannelAccount> pair)
        {
            telemetry.TrackTrace("Hit the NotifyPair method at: " + DateTime.Now.ToString());

            var teamsPerson1 = pair.Item1.AsTeamsChannelAccount();
            var teamsPerson2 = pair.Item2.AsTeamsChannelAccount();
            var connectorClient = new ConnectorClient(new Uri(serviceUrl));

            // Fill in person1's info in the card for person2
            var cardForPerson2 = PairUpNotificationAdaptiveCard.GetCard(teamName, teamsPerson1.Name, teamsPerson1.GivenName, teamsPerson2.GivenName, teamsPerson1.UserPrincipalName);

            // Fill in person2's info in the card for person1
            var cardForPerson1 = PairUpNotificationAdaptiveCard.GetCard(teamName, teamsPerson2.Name, teamsPerson2.GivenName, teamsPerson1.GivenName, teamsPerson2.UserPrincipalName);

            telemetry.TrackTrace($"Notifying user - {teamsPerson1.ObjectId}, {teamsPerson1.GivenName}");
            await NotifyUser(serviceUrl, cardForPerson1, teamsPerson1, tenantId, connectorClient);

            telemetry.TrackTrace($"Notifying user - {teamsPerson2.ObjectId}, {teamsPerson2.GivenName}");
            await NotifyUser(serviceUrl, cardForPerson2, teamsPerson2, tenantId, connectorClient);
        }

        private static async Task NotifyUser(string serviceUrl, string cardToSend, ChannelAccount user, string tenantId, ConnectorClient client)
        {
            telemetry.TrackTrace("Hit the NotifyUser method at: " + DateTime.Now.ToString());

            var me = new ChannelAccount()
            {
                Id = CloudConfigurationManager.GetSetting("MicrosoftAppId"),
                Name = CloudConfigurationManager.GetSetting("BotDisplayName")
            };

            MicrosoftAppCredentials.TrustServiceUrl(serviceUrl);

            // ensure conversation exists
            var response = client.Conversations.CreateOrGetDirectConversation(me, user, tenantId);

            // construct the activity we want to post
            var activity = new Activity()
            {
                Type = ActivityTypes.Message,
                Conversation = new ConversationAccount()
                {
                    Id = response.Id,
                },
                Attachments = new List<Attachment>()
                    {
                        new Attachment()
                        {
                            ContentType = "application/vnd.microsoft.card.adaptive",
                            Content = JsonConvert.DeserializeObject(cardToSend),
                        }
                    }
            };

            var isTesting = bool.Parse(CloudConfigurationManager.GetSetting("Testing"));

            if (!isTesting)
            {
                // shoot the activity over
                await client.Conversations.SendToConversationAsync(activity, response.Id);
            }
        }

        private static async Task<TeamsChannelAccount[]> GetTeamMembers(string serviceUrl, string teamId, string tenantId, ConnectorClient connectorClient)
        {
            MicrosoftAppCredentials.TrustServiceUrl(serviceUrl);

            // Pull the roster of specified team and then remove everyone who has opted out explicitly
#pragma warning disable CS0618 // Type or member is obsolete
            var members = await connectorClient.Conversations.GetTeamsConversationMembersAsync(teamId, tenantId);
#pragma warning restore CS0618 // Type or member is obsolete
            return members;
        }

        private static async Task<List<ChannelAccount>> GetOptedInUsers(TeamInstallInfo teamInfo)
        {
            telemetry.TrackTrace("Hit the GetOptedInUsers method at: " + DateTime.Now.ToString());
            var connectorClient = new ConnectorClient(new Uri(teamInfo.ServiceUrl));
            var optedInUsers = new List<ChannelAccount>();

            var members = await GetTeamMembers(teamInfo.ServiceUrl, teamInfo.TeamId, teamInfo.TenantId, connectorClient);

            if (members.Length > 1)
            {
                telemetry.TrackTrace($"There are {members.Length} members found in {teamInfo.TeamId} at: " + DateTime.Now.ToString());
            }
            else
            {
                telemetry.TrackTrace("There are not enough members found: " + DateTime.Now.ToString());
            }

            foreach (var member in members)
            {
                var optInStatus = IcebreakerBotDataProvider.GetUserOptInStatus(teamInfo.TenantId, member.ObjectId);

                if (optInStatus == null || optInStatus.OptedIn)
                {
                    telemetry.TrackTrace($"Adding {member.Name} to the list at: " + DateTime.Now.ToString());
                    optedInUsers.Add(member);
                }
            }

            return optedInUsers;
        }

        private static List<Tuple<ChannelAccount, ChannelAccount>> MakePairs(List<ChannelAccount> users)
        {
            telemetry.TrackTrace("Hit the MakePairs method at: " + DateTime.Now.ToString());

            if (users.Count > 1)
            {
                telemetry.TrackTrace($"There could be {users.Count / 2} pairs that could be made at: " + DateTime.Now.ToString());
            }
            else
            {
                telemetry.TrackTrace($"Pairs could not be made because of having - {users.Count} at: " + DateTime.Now.ToString());
            }

            var pairs = new List<Tuple<ChannelAccount, ChannelAccount>>();

            Randomize<ChannelAccount>(users);

            for (int i = 0; i < users.Count - 1; i += 2)
            {
                pairs.Add(new Tuple<ChannelAccount, ChannelAccount>(users[i], users[i + 1]));
            }

            return pairs;
        }

        private static void Randomize<T>(IList<T> items)
        {
            Random rand = new Random(Guid.NewGuid().GetHashCode());

            // For each spot in the array, pick
            // a random item to swap into that spot.
            for (int i = 0; i < items.Count - 1; i++)
            {
                int j = rand.Next(i, items.Count);
                T temp = items[i];
                items[i] = items[j];
                items[j] = temp;
            }
        }
    }
}