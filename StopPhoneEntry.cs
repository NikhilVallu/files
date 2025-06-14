using AdaptiveExpressions.Properties;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Adaptive;
using Microsoft.Bot.Builder.Dialogs.Adaptive.Actions;
using Microsoft.Bot.Builder.Dialogs.Adaptive.Conditions;
using Microsoft.Bot.Builder.Dialogs.Adaptive.Input;
using Microsoft.Bot.Builder.Dialogs.Adaptive.Templates;
using PPL.CEX.EussClient.StartStopMove.Models;
using PPL.Omex.Bot.BusinessLogic.Services.Outage;
using PPL.Omex.Bot.Dialogs.Common;
using PPL.Omex.Bot.Interfaces.Recognizer;
using PPL.Omex.Bot.Interfaces.StateManagement;
using PPL.Omex.Bot.LanguageGeneration;
using PPL.Omex.Bot.Models;
using PPL.Omex.Bot.Models.Constants;
using PPL.Omex.Bot.Models.SharedServices;
using PPL.Omex.Bot.Utilities;

namespace PPL.Omex.Bot.Dialogs.StartStopMove
{
    public class StopPhoneEntry : OmexComponentDialog
    {
        private readonly Confirmation _confirmationDialog;
        private readonly IAlertService _alertService;
        public const string TemplateFilePath = "./Dialogs/StartStopMove/StopPhoneEntry.lg";

        public StopPhoneEntry() : base(nameof(StopPhoneEntry))
        { }

        public StopPhoneEntry(
            IOmexStateWrapper stateWrapper,
            IBotTelemetryClient botTelemetryClient,
            IOmExRecognizer recognizerWrapper,
            IOmexLanguageGeneratorFactory languageGeneratorFactory,
            Confirmation confirmationDialog,
            IAlertService alertService)
            : base(nameof(StopPhoneEntry), stateWrapper: stateWrapper, botTelemetryClient: botTelemetryClient,
                recognizerWrapper: recognizerWrapper, languageGeneratorFactory: languageGeneratorFactory)
        {
            _confirmationDialog = confirmationDialog;
            _alertService = alertService;

            Initialize();
        }

        public override void Initialize()
        {
            RecognizerWrapper.AddIntentSet(RecognizerIntentSets.NumberEntrySet);
            RecognizerWrapper.AddIntentSet(RecognizerIntentSets.AgentTransferSet);

            var stopPhoneEntryDialog = new AdaptiveDialog(nameof(StopPhoneEntry))
            {
                Generator = LanguageGeneratorFactory.GetGenerator(TemplateFilePath),
                Recognizer = RecognizerWrapper.GetRecognizer(),
                Triggers = new List<OnCondition>()
                {
                    new OnBeginDialog()
                    {
                        Actions = new List<Dialog>()
                        {
                            LogTraceActivity(EventLogType.StopPhoneEntry, "StopPhoneEntry - Start Flow", EventAction.Start),
                            new TextInput()
                            {
                                Prompt = new ActivityTemplate("${conversation.isFromReconnect == true ? AskPhoneNumberForReconnect() : AskPhoneNumber()}"),
                                Property = "conversation.PhoneEntry",
                                MaxTurnCount = 2,
                                AllowInterruptions = true,
                                Validations = new List<BoolExpression>()
                                {
                                    1 == 0
                                }
                            },
                           // new SendActivity("${UnableToUpdatePhoneNumber()}"),
                            LogTraceActivity(EventLogType.StopPhoneEntry, "StopPhoneEntry - End Flow", EventAction.Error),
                            new IfCondition()
                            {
                                Condition = "conversation.istransferring == true",
                                Actions = new List<Dialog>()
                                {
                                    HandOffActionEx(HandOff.MoveService)
                                },
                                ElseActions = new List<Dialog>()
                                {
                                    HandOffActionEx(HandOff.StopService)
                                }
                            }
                        }
                    },
                    new OnIntent()
                    {
                        Intent = NumberEntryIntents.NoNumberFound
                    },
                    new OnIntent()
                    {
                        Intent = NumberEntryIntents.NumberFound,
                        Actions = new List<Dialog>()
                        {
                            new IfCondition()
                            {
                                Condition = "equals(length(@number), 10)",
                                Actions = new List<Dialog>()
                                {
                                    new OmexSetConversationPropertyAction("PhoneEntry", "=@number"),
                                    new CodeAction(ValidatePhoneNumber),
                                    new IfCondition()
                                    {
                                        Condition = "and(conversation.phonenumberstatus.isvalid == true, " +
                                                    "or(conversation.phonenumberstatus.iswireless == true,conversation.phonenumberstatus.islandline == true))",
                                        Actions = new List<Dialog>()
                                        {
                                            new CodeAction(SetupPhoneNumberConfirmation),
                                            new BeginDialog(nameof(Confirmation)),
                                            new IfCondition()
                                            {
                                                Condition = RecognizedIntentCondition(ConfirmationIntents.Confirmation),
                                                Actions = new List<Dialog>()
                                                {
                                                    // only after customer confirms the phone number,set it to PhoneNumber model and return to parent                                            
                                                    new CodeAction(SetPhoneNumberAsPrimaryPhone),
                                                    LogTraceActivity(EventLogType.StopPhoneEntry, "StopPhoneEntry - End Flow", EventAction.Success),
                                                    new EndDialog()
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    },
                    new OnIntent()
                    {
                        Intent = BaseIntents.None
                    },
                    new OnIntent()
                    {
                        Intent = BaseIntents.NoInput
                    },
                    new OnIntent()
                    {
                        Intent = BaseIntents.DidNotUnderstand
                    },
                    new OnIntent()
                    {
                        Intent = AgentTransferIntents.TransferToAgent,
                        Actions = new List<Dialog>()
                        {
                            new IfCondition()
                            {
                                Condition = "conversation.istransferring == true",
                                Actions = new List<Dialog>()
                                {
                                    HandOffActionEx(HandOff.MoveService)
                                },
                                ElseActions = new List<Dialog>()
                                {
                                    HandOffActionEx(HandOff.StopService)
                                }
                            }
                        }
                    }
                }
            };

            AddDialog(stopPhoneEntryDialog);
            AddDialog(_confirmationDialog);
            InitialDialogId = nameof(StopPhoneEntry);
        }

        #region private
        private async Task<DialogTurnResult> ValidatePhoneNumber(DialogContext dc, object options)
        {
            var inputPhoneNumber = await StateWrapper.GetPropertyAsync<string>(dc, "PhoneEntry");
            var phoneInfo = await _alertService.CheckPhoneStatus(inputPhoneNumber);

            // Update state with Phone Info
            await StateWrapper.SetPropertyAsync(dc.Context, nameof(PhoneNumberStatus), phoneInfo);
            // Return
            return await dc.ContinueDialogAsync();
        }

        private async Task<DialogTurnResult> SetupPhoneNumberConfirmation(DialogContext dc, object options)
        {
            await dc.Context.SetConfirmationPromptParameters(StateWrapper, "${ConfirmPhoneNumber()}", 3, true);
            return await dc.ContinueDialogAsync();
        }

        private async Task<DialogTurnResult> SetPhoneNumberAsPrimaryPhone(DialogContext dc, object options)
        {
            var disconnectRequest = await StateWrapper.GetPropertyAsync<SharedProcessDisconnectRequest>(dc, nameof(SharedProcessDisconnectRequest));
            var inputPhoneNumber = await StateWrapper.GetPropertyAsync<string>(dc, "PhoneEntry");
            var primaryPhoneNumber = new Telephone
            {
                AreaCode = inputPhoneNumber.ToAreaCode(),
                PhoneNumber = inputPhoneNumber.ToPhoneNumber()
            };

            disconnectRequest.PrimaryPhone = primaryPhoneNumber;

            await StateWrapper.SetPropertyAsync(dc.Context, nameof(SharedProcessDisconnectRequest), disconnectRequest);

            return await dc.ContinueDialogAsync();
        }
        #endregion
    }
}
