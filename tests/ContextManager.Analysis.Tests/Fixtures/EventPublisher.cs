using System;

namespace ContextManager.Analysis.Tests.Fixtures;

public class EventPublisher
{
    public event EventHandler? Started;

    // Multi-declarator field event — one entry per declarator
    public event EventHandler<string>? Progressed, Completed;

    // Accessor-form event
    public event EventHandler? Custom
    {
        add { }
        remove { }
    }

    private event EventHandler? InternalOnly;

    public void Publish() { }
}
