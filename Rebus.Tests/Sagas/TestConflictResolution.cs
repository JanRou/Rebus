﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Activation;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Logging;
using Rebus.Pipeline;
using Rebus.Sagas;
using Rebus.Tests.Extensions;
using Rebus.Transport.InMem;
#pragma warning disable 1998

namespace Rebus.Tests.Sagas
{
    [TestFixture]
    public class TestConflictResolution : FixtureBase
    {
        BuiltinHandlerActivator _builtinHandlerActivator;
        ListLoggerFactory _loggerFactory;
        IBus _bus;

        protected override void SetUp()
        {
            _builtinHandlerActivator = Using(new BuiltinHandlerActivator());

            _loggerFactory = new ListLoggerFactory();

            _bus = Configure.With(_builtinHandlerActivator)
                .Logging(l => l.Use(_loggerFactory))
                .Transport(t => t.UseInMemoryTransport(new InMemNetwork(), "will_experience_conflicts"))
                .Options(o => o.SetNumberOfWorkers(10).SetMaxParallelism(10))
                .Start();
        }

        [Test]
        public async Task ItWorks()
        {
            const int messageCount = 3;

            var resetEvent = new ManualResetEvent(false);

            _builtinHandlerActivator.Handle<AllDone>(async i => resetEvent.Set());

            _builtinHandlerActivator.Register((bus, messageContext) => new MySaga(messageCount, messageContext, bus));

            var tasks = Enumerable.Range(0, messageCount)
                .Select(i => string.Format("message-{0}", i))
                .Select(async msg => await _bus.SendLocal(msg));

            await Task.WhenAll(tasks);

            await Task.Delay(3000);

            var linesWithInfoOrAbove = _loggerFactory.Where(l => l.Level > LogLevel.Debug);

            Console.WriteLine(string.Join(Environment.NewLine, linesWithInfoOrAbove.Select(l => l.Text.Limit(120000))));

            resetEvent.WaitOrDie(TimeSpan.FromSeconds(4), "Did not receive the AllDone message!! One or more messages must have been moved to the error queue!");

            var warnings = _loggerFactory.Count(l => l.Level == LogLevel.Warn);

            Assert.That(warnings, Is.EqualTo(0), "Expected no warnings because all conflicts should have been resolved");
        }

        class MySagaData : ISagaData
        {
            public const string ConstantCorrelationId = "hej";

            public MySagaData()
            {
                CorrelationId = ConstantCorrelationId;
                IdsOfHandledMessages = new HashSet<string>();
            }

            public Guid Id { get; set; }
            public int Revision { get; set; }
            public string CorrelationId { get; set; }

            public HashSet<string> IdsOfHandledMessages { get; set; }
        }

        class MySaga : Saga<MySagaData>, IAmInitiatedBy<string>
        {
            readonly int _targetMessageCount;
            readonly IMessageContext _messageContext;
            readonly IBus _bus;

            public MySaga(int targetMessageCount, IMessageContext messageContext, IBus bus)
            {
                _targetMessageCount = targetMessageCount;
                _messageContext = messageContext;
                _bus = bus;
            }

            protected override void CorrelateMessages(ICorrelationConfig<MySagaData> config)
            {
                config.Correlate<string>(s => MySagaData.ConstantCorrelationId, d => d.CorrelationId);
            }

            protected override async Task ResolveConflict(MySagaData otherSagaData)
            {
                foreach (var id in otherSagaData.IdsOfHandledMessages)
                {
                    Data.IdsOfHandledMessages.Add(id);
                }

                Console.WriteLine("Hot diggity!! Merging {0} into {1}", otherSagaData.Revision, Data.Revision);

                await PossiblyComplete();
            }

            public async Task Handle(string message)
            {
                var messageId = _messageContext.Message.GetMessageId();

                Console.WriteLine("ADDING {0}", messageId);

                Data.IdsOfHandledMessages.Add(messageId);

                await Task.Delay(100);

                await PossiblyComplete();
            }

            async Task PossiblyComplete()
            {
                if (Data.IdsOfHandledMessages.Count < _targetMessageCount) return;

                await _bus.SendLocal(new AllDone());
            }

        }

        class AllDone { }
    }
}