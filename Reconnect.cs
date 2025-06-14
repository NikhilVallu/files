using AdaptiveExpressions.Properties;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Adaptive;
using Microsoft.Bot.Builder.Dialogs.Adaptive.Actions;
using Microsoft.Bot.Builder.Dialogs.Adaptive.Conditions;
using PPL.Omex.Bot.BusinessLogic.Services.Payment;
using PPL.Omex.Bot.Dialogs.Common;
using PPL.Omex.Bot.Dialogs.Payment.HouseholdInfo;
using PPL.Omex.Bot.Dialogs.PredictiveIntent.Conditions;
using PPL.Omex.Bot.Dialogs.StartStopMove;
using PPL.Omex.Bot.Interfaces.StateManagement;
using PPL.Omex.Bot.LanguageGeneration;
using PPL.Omex.Bot.Models;
using PPL.Omex.Bot.Models.Constants;

namespace PPL.Omex.Bot.Dialogs.Payment
{
    public class Reconnect : OmexComponentDialog
    {
        private readonly IReconnectService reconnectService;
        private readonly IFinancialStatementService financialStatementService;
        private readonly StopPhoneEntry stopPhoneEntryDialog;
        private readonly KeyWord keywordDialog;
        private readonly Confirmation confirmationDialog;
        private readonly IPayAssistReconnectHelper payAssistReconnectHelper;
        private readonly MedicalCertification medicalCertification;
        private readonly Reco1 reco1Dialog;
        private readonly Reco2 reco2Dialog;
        private readonly FinancialInfoHouseholdPop financialInfoHouseholdPopDialog;

        private const string ShouldPromptForIncome = "ShouldPromptForIncome";

        private const string EligibleReconnectEvent = "EligibleReconnectEvent";
        private const string IneligibleReconnectEvent = "IneligibleReconnectEvent";
        private const string CheckFinancialInfoEvent = "CheckFinancialInfoEvent";
        private const string ProcessFinancialInfoEvent = "ProcessFinancialInfoEvent";
        private const string ProcessReco1Event = "ProcessReco1Event";
        private const string ProcessReco2Event = "ProcessReco2Event";
        private const string ProcessExistingPayPlanEvent = "ProcessExistingPayPlanEvent";
        private const string ProcessCutInPrecautionEvent = "ProcessCutInPrecautionEvent";
        private const string ProcessMedCertOrProtectionEvent = "ProcessMedCertOrProtectionEvent";
        private const string Reco1Event = "Reco1Event";

        public const string TemplateFilePath = "./Dialogs/Payment/Reconnect.lg";

        public Reconnect() : base(nameof(Reconnect)) 
        { }

        public Reconnect(IBotTelemetryClient botTelemetryClient,
            StopPhoneEntry stopPhoneEntryDialog,
            IOmexStateWrapper stateWrapper,
            IReconnectService reconnectService,
            IOmexLanguageGeneratorFactory languageGeneratorFactory,
            KeyWord keyWordDialog,
            Confirmation confirmationDialog,
            MedicalCertification medicalCertification,
            Reco1 reco1Dialog,
            Reco2 reco2Dialog,
            FinancialInfoHouseholdPop financialInfoHouseholdPopDialog,
            IPayAssistReconnectHelper payAssistReconnectHelper,
            IFinancialStatementService financialStatementService)
            : base(nameof(Reconnect), 
                  botTelemetryClient: botTelemetryClient, 
                  stateWrapper: stateWrapper, 
                  recognizerWrapper: null, 
                  languageGeneratorFactory: languageGeneratorFactory)
        {
            this.reconnectService = reconnectService;
            this.stopPhoneEntryDialog = stopPhoneEntryDialog;
            this.keywordDialog = keyWordDialog;
            this.confirmationDialog = confirmationDialog;
            this.payAssistReconnectHelper = payAssistReconnectHelper;
            this.financialStatementService = financialStatementService;
            this.medicalCertification = medicalCertification;
            this.reco1Dialog = reco1Dialog;
            this.reco2Dialog = reco2Dialog;
            this.financialInfoHouseholdPopDialog = financialInfoHouseholdPopDialog;

            Initialize();
        }
        public override void Initialize()
        {
            var reconnectDialog = new AdaptiveDialog(nameof(Reconnect))
            {
                Generator = LanguageGeneratorFactory.GetGenerator(TemplateFilePath),
                Triggers = new List<OnCondition>()
                {
                    new OnBeginDialog()
                    {
                        Actions = new List<Dialog>()
                        {
                            new SetProperty
                            {
                                Property = HandOff.HandOffContext,
                                Value = HandOff.PaymentDropToAgent
                            },
                            new CodeAction(SetupReconnectInformation),

                            new IfCondition()
                            {
                                Condition = "conversation.AccountInformation.IsEligibleForReconnect == true",
                                Actions = new List<Dialog>
                                {
                                    // Eligible for Reconnection
                                    new EmitEvent(EligibleReconnectEvent)
                                },
                                ElseActions = new List<Dialog>
                                {
                                    // Not Eligible for Reconnection
                                    new EmitEvent(IneligibleReconnectEvent)
                                }
                            }
                        }
                    },
                    new OnDialogEvent()
                    {
                        Event = EligibleReconnectEvent,
                        Actions = new List<Dialog>
                        {
                            LogTraceActivity(EventLogType.ReconnectAgreement, "Is Eligible for Reconnect", EventAction.Information),
                            new IfCondition()
                            {
                                Condition = "conversation.PredictedPayAssistOption.HasAgreedReconnectAgreement == true",
                                Actions = new List<Dialog>
                                {
                                    //Has Reconnect Agreement
                                    new EmitEvent(ProcessExistingPayPlanEvent),
                                },
                                ElseActions = new List<Dialog>()
                                {
                                    //Has No Reconnect Agreement
                                    LogTraceActivity(EventLogType.ReconnectAgreement, "Reconnect - Start", EventAction.Start),
                                    new IfCondition()
                                    {
                                        Condition = "conversation.AccountInformation.IsReco1 == true",
                                        Actions = new List<Dialog>
                                        {
                                            new EmitEvent(ProcessReco1Event),
                                        },
                                        ElseActions = new List<Dialog>()
                                        {
                                            new IfCondition()
                                            {
                                                Condition = "conversation.AccountInformation.IsReco2 == true",
                                                Actions = new List<Dialog>
                                                {
                                                    new EmitEvent(ProcessReco2Event),
                                                },
                                                ElseActions = new List<Dialog>()
                                                {
                                                    HandOffActionEx(HandOff.PaymentDropToAgent, TransferReasons.PaymentOther)
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    },
                    new OnDialogEvent()
                    {
                        Event = ProcessExistingPayPlanEvent,
                        Actions = new List<Dialog>
                        {
                            LogTraceActivity(EventLogType.ReconnectAgreement, "Has Reconnect Agreed Agreement", EventAction.Information),
                            new SendActivity("${UnpaidRecoAgreement()}"),
                            //if Security Deposit Included
                            new IfCondition()
                            {
                                Condition = "conversation.accountinformation.HasSecurityDeposit == true",
                                Actions = new List<Dialog>
                                {
                                    LogTraceActivity(EventLogType.ReconnectAgreement, "Has Security Deposit", EventAction.Information),
                                    new SendActivity("${SecurityDepositIncluded()}")
                                }
                            },
                            new CodeAction(SetupMakePaymentNowConfirmation),
                            new BeginDialog(nameof(Confirmation)),
                            new IfCondition()
                            {
                                Condition = RecognizedIntentCondition(ConfirmationIntents.Confirmation),
                                Actions = new List<Dialog>
                                {
                                    new EmitEvent(Events.PayNow, bubble: true)
                                },
                                ElseActions = new List<Dialog>
                                {
                                    HandOffActionEx(HandOff.PaymentDropToAgent, TransferReasons.ReconnectNotAbleToPay)
                                }
                            }
                        }
                    },
                    new OnDialogEvent()
                    {
                        Event = ProcessReco1Event,
                        Actions = new List<Dialog>
                        {
                            LogTraceActivity(EventLogType.ReconnectAgreement, "Eligible For Reco1", EventAction.Information),
                            new CodeAction(SetupPayFullAmount),
                            new EmitEvent(ProcessMedCertOrProtectionEvent),
                            new CodeAction(SetupPayNowOrPaymentPlanKeywordDialog),
                            new BeginDialog(nameof(KeyWord)),                                                 
                            //make payment Payment Plan Options
                            new IfCondition()
                            {
                                // Make Payment / PayNow
                                Condition = RecognizedIntentCondition(KeywordIntents.PredictiveIntentMakePayment),
                                Actions = new List<Dialog>()
                                {
                                    new EmitEvent(Events.PayNow, bubble: true),
                                    new EndDialog()
                                }
                            },
                            new IfCondition()
                            {
                                Condition = RecognizedIntentCondition(KeywordIntents.PredictiveIntentPaymentOptions),
                                Actions = new List<Dialog>
                                {
                                    // User chose to try the Payment Plan - setup payment plan info for the upcoming flows
                                    new CodeAction(SetupPaymentPlan),
                                    new EmitEvent(CheckFinancialInfoEvent),
                                    new EmitEvent(Reco1Event),
                                    new EndDialog()
                                }
                            },
                            HandOffActionEx(HandOff.PaymentDropToAgent, TransferReasons.ReconnectNotAbleToPay)
                        }
                    },
                    new OnDialogEvent()
                    {
                        Event = ProcessReco2Event,
                        Actions = new List<Dialog>
                        {
                            LogTraceActivity(EventLogType.ReconnectAgreement, "Eligible For Reco2", EventAction.Information),
                            new EmitEvent(ProcessMedCertOrProtectionEvent),
                            new OmexSetDialogPropertyAction(ShouldPromptForIncome, true),
                            new EmitEvent(CheckFinancialInfoEvent),
                            new BeginDialog(nameof(Reco2))
                        }
                    },
                    new OnDialogEvent()
                    {
                        Event = ProcessMedCertOrProtectionEvent,
                        Actions = new List<Dialog>
                        {
                            new IfCondition()
                            {
                                Condition = IsFeatureEnabled(Feature.StandardAndProtectionAgreements),
                                Actions = new List<Dialog>()
                                {
                                    // The New Protections Dialog will begin replacing below code here
                                    new SendActivity("Placeholder for Protections"),
                                    HandOffActionEx(HandOff.PaymentDropToAgent, TransferReasons.PaymentOther),
                                },
                                ElseActions = new List<Dialog>()
                                {
                                    new BeginDialog(nameof(MedicalCertification))
                                }
                            }
                        }
                    },
                    new OnDialogEvent()
                    {
                        Event = Reco1Event,
                        Actions = new List<Dialog>
                        {
                            new IfCondition()
                            {
                                Condition = IsFeatureEnabled(Feature.StandardAndProtectionAgreements),
                                Actions = new List<Dialog>()
                                {
                                    // The New RI Reco1 Dialog will begin replacing below code here
                                    new SendActivity("Placeholder for RI Reco1"),
                                    HandOffActionEx(HandOff.PaymentDropToAgent, TransferReasons.PaymentOther)
                                },
                                ElseActions = new List<Dialog>()
                                {
                                    new BeginDialog(nameof(Reco1))
                                }
                            }
                        }
                    },
                    new OnDialogEvent()
                    {
                        Event = CheckFinancialInfoEvent,
                        Actions = new List<Dialog>
                        {
                            new CodeAction(CheckIfFinancialInfoTaken),
                            new IfCondition()
                            {
                                Condition = "conversation.AccountInformation.FinancialStatement.IsEligibleForNewFinancialStatement == true",
                                Actions = new List<Dialog>()
                                {
                                    new IfCondition()
                                    {
                                        Condition = $"dialog.{ShouldPromptForIncome}",
                                        Actions = new List<Dialog>()
                                        {
                                            new CodeAction(SetupIncomeChangedConfirmation),
                                            new BeginDialog(nameof(Confirmation)),
                                            new IfCondition()
                                            {
                                                Condition = RecognizedIntentCondition(ConfirmationIntents.Confirmation),
                                                Actions = new List<Dialog>
                                                {
                                                    new EmitEvent(ProcessFinancialInfoEvent)
                                                }
                                            }
                                        },
                                        ElseActions = new List<Dialog>()
                                        {
                                            new EmitEvent(ProcessFinancialInfoEvent)
                                        }
                                    }
                                }
                            }
                        }
                    },
                    new OnDialogEvent()
                    {
                        Event = ProcessFinancialInfoEvent,
                        Actions = new List<Dialog>
                        {
                            new BeginDialog(nameof(FinancialInfoHouseholdPop)),
                            new CodeAction(ResetReconnectInformation)
                        }
                    },
                    new OnDialogEvent()
                    {
                        Event = IneligibleReconnectEvent,
                        Actions = new List<Dialog>
                        {
                            LogTraceActivity(EventLogType.ReconnectAgreement, "Account not eligible for reconnect", EventAction.Information),
                            new CodeAction(GetCutInOrderDetails),                                                                                                   
                            // Cut In Order Api call failed or not available
                            new IfCondition()
                            {
                                Condition = "conversation.CutInOrder == null",
                                Actions = new List<Dialog>
                                {
                                    HandOffActionEx(HandOff.PaymentDropToAgent, TransferReasons.ReconnectNoCutInOrderIssued),
                                },
                                ElseActions = new List<Dialog>
                                {
                                    // Cut-In Order issued OR Cut-In Order Date in the past
                                    new IfCondition()
                                    {
                                        Condition = "conversation.CutInOrder.IsCutInDateInThePast == true",
                                        Actions = new List<Dialog>
                                        {
                                            HandOffActionEx(HandOff.PaymentDropToAgent, TransferReasons.ReconnectCutInDateInPast)
                                        },
                                        ElseActions = new List<Dialog>
                                        {
                                            new EmitEvent(ProcessCutInPrecautionEvent),
                                            EndOfCallPrompt()
                                        }
                                    }
                                }
                            }
                        }
                    },
                    new OnDialogEvent()
                    {
                        Event = ProcessCutInPrecautionEvent,
                        Actions = new List<Dialog>
                        {
                            new SendActivity("${CutInAfterToday()}"),
                            new IfCondition()
                            {
                                Condition = "conversation.Account.HasGas == true", 
                                Actions = new List<Dialog>
                                {
                                    new SendActivity("${Reconnect.CutInSafetyPrecautionGas()}"),
                                    new SetProperty()
                                    {
                                        Property = "conversation.isFromReconnect",
                                        Value = new ValueExpression(true)
                                    },
                                    new BeginDialog(nameof(StopPhoneEntry)),
                                    //new SendActivity("${Reconnect.AdultAtHomePhoneNumbers()}"),
                                    //new CodeAction(SetupConfirmDateEntered),
                                },
                                ElseActions = new List<Dialog>
                                {
                                    new SendActivity("${CutInSafetyPrecaution()}")
                                }        
                            }
                        }
                    }
                }
            };

            AddDialog(medicalCertification);
            AddDialog(reco1Dialog);
            AddDialog(reco2Dialog);
            AddDialog(financialInfoHouseholdPopDialog);

            AddDialog(reconnectDialog);
            AddDialog(keywordDialog);
            AddDialog(confirmationDialog);

            InitialDialogId = nameof(Reconnect);
        }
        
        private async Task<DialogTurnResult> SetupConfirmDateEntered(DialogContext dc, object options)
        {
            await dc.Context.SetConfirmationPromptParameters(StateWrapper, "${StopDateEntry.ReadBackToCustomer()}", 3, true);
            return await dc.ContinueDialogAsync();
        }

        private async Task<DialogTurnResult> SetupIncomeChangedConfirmation(DialogContext dc, object options)
        {
            await dc.Context.SetConfirmationPromptParameters(StateWrapper, "${Reconnect.HasIncomeChanged()}", 3, true);
            return await dc.ContinueDialogAsync();
        }

        private async Task<DialogTurnResult> SetupMakePaymentNowConfirmation(DialogContext dc, object options)
        {
            await dc.Context.SetConfirmationPromptParameters(StateWrapper, "${Reconnect.MakePaymentNow()}", 3, true);
            return await dc.ContinueDialogAsync();
        }
        private async Task<DialogTurnResult> SetupPayNowOrPaymentPlanKeywordDialog(DialogContext dc, object options)
        {
            var keywordParameters = new KeywordParameters
            {
                PromptTemplate = "${Reconnect.MakePaymentOrPlanPayment()}",
                ScopedIntents = new List<string>()
                {
                    KeywordIntents.PredictiveIntentMakePayment,
                    KeywordIntents.PredictiveIntentPaymentOptions,
                    KeywordIntents.DidNotUnderstand,
                    KeywordIntents.KeywordUnsure
                },
                AllowHandOff = true,
                MaxTurnCount = 3
            };
            await dc.Context.SetKeywordParameters(StateWrapper, keywordParameters);
            return await dc.ContinueDialogAsync();
        }

        private async Task<DialogTurnResult> SetupReconnectInformation(DialogContext dc, object options)
        {
            try
            {
                // Initialize PredictedPayAssistOption
                await payAssistReconnectHelper.InitPayAssist(dc);
            }
            catch (Exception ex)
            {
                // Handle API failures globally
                throw new Exception("API Call - GetReconnectInformation Failed", ex);
            }
            // Return
            return await dc.ContinueDialogAsync();
        }

        private async Task<DialogTurnResult> GetCutInOrderDetails(DialogContext dc, object options)
        {
            //Get Account Info
            var accountInformation = await StateWrapper.GetPropertyAsync<AccountInformation>(dc, nameof(AccountInformation));

            // Make CSA Api call to check reconnection eligibility
            var result = await reconnectService.GetCutInOrderDetails(accountInformation.Account.Id);

            // Update state
            await StateWrapper.SetPropertyAsync(dc, nameof(CutInOrder), result);

            // Return
            return await dc.ContinueDialogAsync();
        }
        private async Task<DialogTurnResult> CheckIfFinancialInfoTaken(DialogContext dc, object options)
        {
            //Get Account Info
            var accountInformation = await StateWrapper.GetPropertyAsync<AccountInformation>(dc, nameof(AccountInformation));

            if (accountInformation.FinancialStatement == null)
            {
                accountInformation.FinancialStatement = await financialStatementService.GetFinancialStatement(accountInformation.Account.Id);
                await StateWrapper.SetPropertyAsync(dc.Context, nameof(AccountInformation), accountInformation);
            }

            // Return
            return await dc.ContinueDialogAsync();
        }

        private async Task<DialogTurnResult> SetupPayFullAmount(DialogContext dc, object options)
        {
            await payAssistReconnectHelper.SetupFullAmountOneTimePayment(dc);
            // Return
            return await dc.ContinueDialogAsync();
        }

        private async Task<DialogTurnResult> SetupPaymentPlan(DialogContext dc, object options)
        {
            await payAssistReconnectHelper.SetupReconnectPaymentPlan(dc);
            // Return
            return await dc.ContinueDialogAsync();
        }

        private async Task<DialogTurnResult> ResetReconnectInformation(DialogContext dc, object options)
        {
            try
            {
                //clear the current option
                await StateWrapper.DeletePropertyAsync<ReconnectOption>(dc, StateProperties.PredictedPayAssistOption);

                var accountInformation = await StateWrapper.GetPropertyAsync<AccountInformation>(dc, nameof(AccountInformation));
                accountInformation.ReconnectOptions = null;
                await StateWrapper.SetPropertyAsync(dc.Context, nameof(AccountInformation), accountInformation);

                // Initialize PredictedPayAssistOption
                await payAssistReconnectHelper.InitPayAssist(dc);
            }
            catch (Exception ex)
            {
                // Handle API failures globally
                throw new Exception("API Call - GetReconnectInformation Failed", ex);
            }
            // Return
            return await dc.ContinueDialogAsync();
        }
    }
}
