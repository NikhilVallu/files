using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Testing.XUnit;
using Moq;
using PPL.Omex.Bot.Models;
using PPL.Omex.Bot.BusinessLogic.Services.Payment;
using PPL.Omex.Bot.Csa.ApiModels;
using PPL.Omex.Bot.Dialogs;
using PPL.Omex.Bot.Dialogs.Common;
using PPL.Omex.Bot.Dialogs.Payment;
using PPL.Omex.Bot.Dialogs.Payment.HouseholdInfo;
using PPL.Omex.Bot.Dialogs.PredictiveIntent.Conditions;
using PPL.Omex.Bot.Dialogs.StartStopMove;
using PPL.Omex.Bot.Interfaces.StateManagement;
using PPL.Omex.Bot.Tests.Common.Dialogs;
using PPL.Omex.Bot.Utilities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MassTransit.SagaStateMachine;
using PPL.Omex.Bot.Models.Constants;
using PPL.Omex.TwilioServices.Client.Models;
using Xunit;
using Xunit.Abstractions;
using FinancialStatement = PPL.Omex.Bot.Models.FinancialStatement;
using PaymentAgreement = PPL.Omex.Bot.Models.PaymentAgreement;

namespace PPL.Omex.Bot.Tests.Dialogs.Payment
{
    [Collection("Sequential")]
    public class ReconnectTests : OmexDialogTestBase
    {
        private const string ExpectedAgentHandOffEvent = "hand_off received by parent";
        private const string ExpectedBackToFlow = "Child dialog ended";
        private const string ExpectedPowerWillBeOnSoonMessage = "However, your services will be turned back on within the next 24 hours.";
        private const string ExpectedGasAdultPhoneMessage = "An adult (18 years or older) must be present when the technician arrives to ensure we can access your pilot lights and we'll need their phone number.";
        private const string PlaceholderEndOfStory = "You reached gas reconnect phone entry placeHolder.";

        private Mock<IReconnectService> mockReconnectService;
        private Confirmation mockConfirmationDialog;
        private KeyWord mockKeyWordDialog;
        private MedicalCertification mockMedicalCertDialog;
        private Mock<IFinancialStatementService> mockFinancialStatementService;
        private Mock<IPayAssistReconnectHelper> mockPayAssistReconnectHelper;
        private FinancialInfoHouseholdPop mockFinancialHouseholdInfoPopDialog;
        private Reco1 mockReco1;
        private Reco2 mockReco2;
        private StopPhoneEntry mockStopPhoneEntryDialog;

        public ReconnectTests(ITestOutputHelper testOutputHelper) : base(testOutputHelper)
        {
            InitializeTests();
        }

        public class FlowData : TestFlowData
        {
            public long AccountId { get; set; }

            #region Reconnection Data
            public string Id { get; set; }
            public bool? IsEligible { get; set; }
            public bool? HasCutInOrderIssued { get; set; }
            public DateTime? CutInOrderDate { get; set; }
            public bool? HasReconnectAgreement { get; set; }
            public double? AgreementTotal { get; set; }
            public double SecurityDeposit { get; set; }
            public double AmountIncludedFromSecurityDeposit { get; set; }
            public string ReconnectOptionType { get; set; }
            public string LastFinancialStatementUpdated { get; set; }
            
            public string OpCo = "PA";
            #endregion

            #region Financial Statement Data            
            public DateTime LastUpdatedDate { get; set; }
            public bool IsEligibleForNewFinancialStatement { get; set; }
            public bool IsGasAccount { get; set; } = false;
            #endregion
        }

        private void InitializeTests()
        {
            mockReconnectService = new Mock<IReconnectService>();
            mockFinancialStatementService = new Mock<IFinancialStatementService>();
            mockPayAssistReconnectHelper = new Mock<IPayAssistReconnectHelper>();

            TestNlpBot.WhenTextIs("payment plan options").IntentIs(KeywordIntents.PredictiveIntentPaymentOptions);
            TestNlpBot.WhenTextIs("make payment").IntentIs(KeywordIntents.PredictiveIntentMakePayment);

        }

        private void SetupChildDialogs(FlowData flowData)
        {
            mockConfirmationDialog = new Confirmation();
            mockConfirmationDialog.AsSimpleChildDialog(nameof(Confirmation), MockOmexRecognizer.Object)
                .EndDialogOnIntent("confirmation")
                .EndDialogOnIntent("decline")
                .DoneSimpleChildDialogSetup();

            mockMedicalCertDialog = new MedicalCertification();
            mockMedicalCertDialog.AsSimpleChildDialog(nameof(MedicalCertification), MockOmexRecognizer.Object)
                .DoneSimpleChildDialogSetup();

            mockKeyWordDialog = new KeyWord();
            mockKeyWordDialog.AsSimpleChildDialog(nameof(KeyWord), MockOmexRecognizer.Object)
                .EndWithCallbackOnIntent(KeywordIntents.PredictiveIntentMakePayment,
            async (DialogContext dc) =>
            {
                await StateWrapper.SetPropertyAsync(dc, "keyWordintentresult", KeywordIntents.PredictiveIntentMakePayment);
                return await dc.ContinueDialogAsync();
            })
                .EndWithCallbackOnIntent(KeywordIntents.PredictiveIntentPaymentOptions,
            async (DialogContext dc) =>
            {
                await StateWrapper.SetPropertyAsync(dc, "keyWordintentresult", KeywordIntents.PredictiveIntentPaymentOptions);
                return await dc.ContinueDialogAsync();
            })
            .DoneSimpleChildDialogSetup();

            mockFinancialHouseholdInfoPopDialog = new FinancialInfoHouseholdPop();
            mockFinancialHouseholdInfoPopDialog.AsSimpleChildDialog(nameof(FinancialInfoHouseholdPop), MockOmexRecognizer.Object)
                .DoneSimpleChildDialogSetup();

            mockReco1 = new Reco1();
            mockReco1.AsSimpleChildDialog(nameof(Reco1), MockOmexRecognizer.Object)
                .DoneSimpleChildDialogSetup();
            
            mockReco2 = new Reco2();
            mockReco2.AsSimpleChildDialog(nameof(Reco2), MockOmexRecognizer.Object)
                .DoneSimpleChildDialogSetup();
            
                mockStopPhoneEntryDialog = new StopPhoneEntry();
                mockStopPhoneEntryDialog.AsSimpleChildDialog(nameof(StopPhoneEntry), MockOmexRecognizer.Object)
                    .DoneSimpleChildDialogSetup();
        }

        private async Task SetupTurn(FlowData flowData, IOmexStateWrapper omexStateWrapper, ITurnContext turnContext)
        {
            var predictedPayAssistOption = new ReconnectOption();
            var accountInformation = new AccountInformation()
            {
                Account = new Account()
                {
                    Id = 123456789,
                    Services = new List<Service>()
                    {
                        new Service()
                        {
                            Id = 1,
                            ServiceType = flowData.IsGasAccount ? "0100" : "0200"
                        }
                    }
                }
            };

            if (flowData.IsEligible ?? false)
            {
                accountInformation.ReconnectOptions = new List<ReconnectOption>()
                {
                    predictedPayAssistOption
                };
            }

            if ((flowData.HasReconnectAgreement ?? false) && (flowData.IsEligible ?? false))
            {
                accountInformation.PaymentAgreements = new List<PaymentAgreement>()
                {
                    new PaymentAgreement()
                    {
                        IsActive = true,
                        AgreementType = Csa.ApiModels.Constants.PaymentAgreementTerm.Reconnect,
                        AgreementTotal = flowData.AgreementTotal ?? 0
                    }
                };

                predictedPayAssistOption.OptionCode = Csa.ApiModels.Constants.ReconnectOptions.PAY_AGREED_DUE;
                predictedPayAssistOption.MinimumDownPaymentDue = flowData.AgreementTotal ?? 0;
            }
            else if (flowData.IsEligible ?? false)
            {
                predictedPayAssistOption.AgreementType = flowData.ReconnectOptionType;
                predictedPayAssistOption.OptionCode = flowData.ReconnectOptionType == "RC"
                    ? Csa.ApiModels.Constants.PaymentAgreementType.RECONNECT
                    : Csa.ApiModels.Constants.PaymentAgreementType.RECONNECT_TWO;
                predictedPayAssistOption.Amount = (decimal)(flowData.AgreementTotal ?? 0.0);
            }

            if (flowData.SecurityDeposit > 0)
            {
                var accountSummary = new AccountSummary();
                accountSummary.DeferredCharges = flowData.SecurityDeposit;
                accountInformation.AccountSummary = accountSummary;
            }
            
            await StateWrapper.SetPropertyAsync(turnContext, nameof(StateProperties.OpCo), flowData.OpCo);
            var features = new List<OpCoFeature>();
            if (flowData.OpCo == Constants.Constants.OpCo.PA)
            {
                features.Add(new OpCoFeature { Active = true, FeatureId = 26, Id = 28, OpCoCode = "PA" });
            }
            await StateWrapper.SetPropertyAsync(turnContext, StateProperties.OpCoFeatures, features);

            await StateWrapper.SetPropertyAsync(turnContext, nameof(AccountInformation), accountInformation);
            await StateWrapper.SetPropertyAsync(turnContext, nameof(Account), accountInformation.Account);
            await StateWrapper.SetPropertyAsync(turnContext, StateProperties.PredictedPayAssistOption, predictedPayAssistOption);
            await StateWrapper.SetPropertyAsync(turnContext, "session.MainMenuStarted", true);

            await Task.FromResult(true);
        }

        private void SetupReconnectService(FlowData flowData)
        {
            var cutInOrder = new CutInOrder()
            {
                CutInOrderDate = flowData.CutInOrderDate
            };
            mockReconnectService.Setup(i => i.GetCutInOrderDetails(It.IsAny<long>()))
                .ReturnsAsync(cutInOrder);
        }

        private void SetupHouseholdInfoService(FlowData flowData)
        {
            var finData = new FinancialStatement()
            {
                AccountId = flowData.AccountId,
                IsEligibleForNewFinancialStatement = flowData.IsEligibleForNewFinancialStatement,
                //LastUpdatedDate = flowData.LastUpdatedDate
            };
            mockFinancialStatementService.Setup(i => i.GetFinancialStatement(It.IsAny<long>()))
                .ReturnsAsync(finData);
        }

        /// <summary>
        /// Define the data to be used during the TestFlow 
        /// Enumeration of individual TestCase Data
        /// </summary>
        private static class FlowDataGenerator
        {
            public static IEnumerable<object[]> TestCases()
            {
                var testCaseData = new FlowData();

                //Test Case 1
                testCaseData = new FlowData()
                {
                    Test = "RecoEligible_HasRecoAgreement_IncludesSecDeposit_Should_ReplyWith_AgreementTotalAndAmtIncludedFromSecDeposit",
                    IsEligible = true,
                    HasReconnectAgreement = true,
                    AgreementTotal = 100.20,
                    SecurityDeposit = 500.00
                };
                testCaseData.Turns = new Turn[]
                {
                    new Turn { N = "Security Deposit Included", U = null, R = "Your service will be turned back on once you pay " + testCaseData.AgreementTotal.ToOmexCurrency() + "." },
                    new Turn { U = null, R = "This includes " + (testCaseData.SecurityDeposit/2).ToOmexCurrency() + ", which is half of your security deposit." }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 2
                testCaseData = new FlowData()
                {
                    Test = "RecoEligible_HasRecoAgreement_NoSecDeposit_Should_ReplyWith_AgreementTotalOnly",
                    IsEligible = true,
                    HasReconnectAgreement = true,
                    AgreementTotal = 100.20,
                    SecurityDeposit = 0
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Security Deposit Not Included", U = null, R = "Your service will be turned back on once you pay " + testCaseData.AgreementTotal.ToOmexCurrency() + "." },
                        new Turn { U = null, R = "Message from Confirmation dialog" }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 3
                testCaseData = new FlowData()
                {
                    Test = "Reconnection_Eligible_HasRecoAgreement_PayNow_Confirmation",
                    IsEligible = true,
                    HasReconnectAgreement = true,
                    AgreementTotal = 100.00,
                    SecurityDeposit = 0
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Pay Now", U = null, Rs = new List<string> { "Your service will be turned back on once you pay " + testCaseData.AgreementTotal.ToOmexCurrency() + ".","Message from Confirmation dialog" } },
                        new Turn { U = "yes", R = "PayNow event received by parent" }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 4
                testCaseData = new FlowData()
                {
                    Test = "RecoEligible_HasNoRecoAgreement_RC_Should_AskForMakeOrPlanPayment",
                    IsEligible = true,
                    HasReconnectAgreement = false,
                    ReconnectOptionType = "RC"
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Reconnect Option RC", U = null, R = "Message from MedicalCertification dialog" },
                        new Turn { U = "yes", R = "Message from KeyWord dialog" },
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 5
                testCaseData = new FlowData()
                {
                    Test = "RecoEligible_HasNoRecoAgreement_RC_PaymentPlan_FinInfoNotTaken_Should_GoToFinInfoThenReco1",
                    IsEligible = true,
                    HasReconnectAgreement = false,
                    ReconnectOptionType = "RC",
                    IsEligibleForNewFinancialStatement = true
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Reconnect Option RC", U = null, R = "Message from MedicalCertification dialog" },
                        new Turn { U = "yes", R = "Message from KeyWord dialog" },
                        new Turn { U = "payment plan options", R = "Message from FinancialInfoHouseholdPop dialog" },
                        new Turn { U = "yes", R = "Message from Reco1 dialog" }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 6
                testCaseData = new FlowData()
                {
                    Test = "RecoEligible_HasNoRecoAgreement_RC_PaymentPlan_FinInfoAlreadyTaken_Should_GoToReco1",
                    IsEligible = true,
                    HasReconnectAgreement = false,
                    ReconnectOptionType = "RC",
                    IsEligibleForNewFinancialStatement = false
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Reconnect Option RC", U = null, R = "Message from MedicalCertification dialog" },
                        new Turn { U = "yes", R = "Message from KeyWord dialog" },
                        new Turn { U = "payment plan options", R = "Message from Reco1 dialog" },
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 7
                testCaseData = new FlowData()
                {
                    Test = "RecoEligible_HasNoRecoAgreement_RC_MakePayment_Should_NavigateToPayNow",
                    IsEligible = true,
                    HasReconnectAgreement = false,
                    ReconnectOptionType = "RC"
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Reconnect Option RC", U = null, R = "Message from MedicalCertification dialog" },
                        new Turn { U = "yes", R = "Message from KeyWord dialog" },
                        new Turn { U = "make payment", R = "PayNow event received by parent" }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 8
                testCaseData = new FlowData()
                {
                    Test = "RecoEligible_HasNoRecoAgreement_RT_FinInfoNotTakenShould_AskIfIncomeChanged",
                    IsEligible = true,
                    HasReconnectAgreement = false,
                    ReconnectOptionType = "RT",
                    LastFinancialStatementUpdated = DateTime.Now.Date.ToShortDateString(),
                    IsEligibleForNewFinancialStatement = true
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Reconnect Option RT", U = null, R = "Message from MedicalCertification dialog" },
                        new Turn { U = "yes", R = "Message from Confirmation dialog" },
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 9
                testCaseData = new FlowData()
                {
                    Test = "RecoEligible_HasNoRecoAgreement_RT_FinInfoNotTaken_IncomeChanged_Should_GoToFinInfoThenReco2",
                    IsEligible = true,
                    HasReconnectAgreement = false,
                    ReconnectOptionType = "RT",
                    LastFinancialStatementUpdated = DateTime.Now.Date.ToShortDateString(),
                    IsEligibleForNewFinancialStatement = true
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Reconnect Option RT", U = null, R = "Message from MedicalCertification dialog" },
                        new Turn { U = "yes", R = "Message from Confirmation dialog" },
                        new Turn { U = "yes", R = "Message from FinancialInfoHouseholdPop dialog" },
                        new Turn { U = "yes", R = "Message from Reco2 dialog" }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 10
                testCaseData = new FlowData()
                {
                    Test = "RecoEligible_HasNoRecoAgreement_RT_FinInfoNotTaken_IncomeNotChanged_Should_GoToReco2",
                    IsEligible = true,
                    HasReconnectAgreement = false,
                    ReconnectOptionType = "RT",
                    LastFinancialStatementUpdated = DateTime.UtcNow.Date.ToShortDateString(),
                    IsEligibleForNewFinancialStatement = true
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Reconnect Option RT", U = null, R = "Message from MedicalCertification dialog" },
                        new Turn { U = "yes", R = "Message from Confirmation dialog" },
                        new Turn { U = "no", R = "Message from Reco2 dialog" }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 11
                testCaseData = new FlowData()
                {
                    Test = "RecoEligible_HasNoRecoAgreement_RT_FinInfoAlreadyTaken_Should_GoToReco2",
                    IsEligible = true,
                    HasReconnectAgreement = false,
                    ReconnectOptionType = "RT",
                    LastFinancialStatementUpdated = DateTime.UtcNow.Date.ToShortDateString(),
                    IsEligibleForNewFinancialStatement = false
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Reconnect Option RT", U = null, R = "Message from MedicalCertification dialog" },
                        new Turn { U = "yes", R = "Message from Reco2 dialog" }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 12
                testCaseData = new FlowData()
                {
                    Test = "Reconnection_NotEligible_CutInOrderIssued_CutInDate_IsNotToday",
                    AccountId = 123456789,
                    IsEligible = false,
                    HasCutInOrderIssued = true,
                    CutInOrderDate = DateTime.Now.Date.AddDays(1)
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Cut-In Order Issued Cut-In Date Not Today", U = null, Rs =
                            new List<string>
                            {
                                ExpectedPowerWillBeOnSoonMessage,
                                "Lastly, we recommend you turn off your main breaker, as well as disconnect any generators before we reconnect service.",
                                ExpectedBackToFlow
                            }
                        }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Test Case 13
                testCaseData = new FlowData()
                {
                    Test = "Reconnection_NotEligible_CutInOrderIssued_CutInDate_IsPast",
                    AccountId = 123456789,
                    IsEligible = false,
                    HasCutInOrderIssued = true,
                    CutInOrderDate = DateTime.Now.Date.AddDays(-2)
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Cut-In Order Issued Cut-In Date Is Past", U = null, R = ExpectedAgentHandOffEvent }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Not Eligible, CutIn Issued, Cut Date >= today, Electric Service
                testCaseData = new FlowData()
                {
                    Test = "Reconnection_NotEligible_CutInOrderIssued_CutInDate_IsToday_Electric",
                    AccountId = 123456789,
                    IsEligible = false,
                    HasCutInOrderIssued = true,
                    CutInOrderDate = DateTime.Now.Date
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Cut-In Order Issued Cut-In Date Is Today", U = null, Rs = 
                            new List<string> 
                            { 
                                ExpectedPowerWillBeOnSoonMessage,
                                "Lastly, we recommend you turn off your main breaker, as well as disconnect any generators before we reconnect service.",
                                ExpectedBackToFlow
                            } 
                        }
                };
                yield return new object[] { new TestDataObject(testCaseData) };

                //Not Eligible, CutIn Issued, Cut Date >= today, Gas Service
                testCaseData = new FlowData()
                {
                    Test = "Reconnection_NotEligible_CutInOrderIssued_CutInDate_IsToday_Gas",
                    AccountId = 123456789,
                    IsEligible = false,
                    HasCutInOrderIssued = true,
                    CutInOrderDate = DateTime.Now.Date,
                    IsGasAccount = true
                };
                testCaseData.Turns = new Turn[]
                {
                        new Turn { N = "Cut-In Order Issued Cut-In Date Is Today", U = null, Rs =
                            new List<string>
                            {
                                ExpectedPowerWillBeOnSoonMessage,
                                ExpectedGasAdultPhoneMessage,
                                PlaceholderEndOfStory,
                                ExpectedBackToFlow
                            }
                        }
                };
                yield return new object[] { new TestDataObject(testCaseData) };
            }
        }
        #region TestParentDialog expected intents, events and messages
        // TestParentDialog expected Intents and corresponding messsages sent on those intents
        Dictionary<string, string> ParentDialogExpectedIntentsAndMessages = new Dictionary<string, string>
        {
        };

        // TestParentDialog expected Events and corresponding messsages sent on those events
        Dictionary<string, string> ParentDialogExpectedEventsAndMessages = new Dictionary<string, string>
        {
            [$"{Events.PayNow}"] = "PayNow event received by parent",
            [$"{Events.HandOff}"] = ExpectedAgentHandOffEvent,
        };
        #endregion

        [Theory]
        [MemberData(nameof(FlowDataGenerator.TestCases), MemberType = typeof(FlowDataGenerator), DisableDiscoveryEnumeration = true)]
        public async Task DialogFlowTest(TestDataObject testDataObject)
        {
            var flowData = testDataObject.GetObject<FlowData>();
            SetupChildDialogs(flowData);
            SetupReconnectService(flowData);
            SetupHouseholdInfoService(flowData);

            var sut = new Reconnect(
                MockBotTelemetryClient.Object,
                mockStopPhoneEntryDialog,
                StateWrapper,
                mockReconnectService.Object,
                MockOmexLanguageGeneratorFactory.Object,
                mockKeyWordDialog,
                mockConfirmationDialog,
                mockMedicalCertDialog,
                mockReco1,
                mockReco2,
                mockFinancialHouseholdInfoPopDialog,
                mockPayAssistReconnectHelper.Object,
                mockFinancialStatementService.Object
            );

            var testFlowExecutor = GetTestFlowExecutor<FlowData>(sut, nameof(Bot.Dialogs.Payment.Reconnect), testDataObject, SetupTurn,
                ParentDialogExpectedIntentsAndMessages, ParentDialogExpectedEventsAndMessages);

            // Act
            await testFlowExecutor.Execute();
        }

    }
}


