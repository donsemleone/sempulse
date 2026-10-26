using System.Diagnostics;

namespace Sempulse.Pet;

// Runs the cat's WinForms window on its own UI thread so its animation never competes with the panel.
public sealed class DesktopPetHost : IDisposable
{
    PetForm? form;
    Thread? thread;
    string mode = "auto";
    bool hidden;

    public bool IsRunning => thread is { IsAlive: true };

    public void Start()
    {
        if (IsRunning) return;
        var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            try
            {
                var pet = new PetForm();
                // Queued so the commands run after the form's Load has placed the cat.
                pet.HandleCreated += (_, _) => pet.BeginInvoke(() =>
                {
                    form = pet;
                    pet.HandleCommand("mode " + mode);
                    if (hidden) pet.HandleCommand("hide");
                    ready.Set();
                });
                System.Windows.Forms.Application.Run(pet);
            }
            catch (Exception exception)
            {
                // The cat is decoration: a failure here must never take the panel down.
                Debug.WriteLine($"Desktop pet stopped: {exception}");
            }
            finally
            {
                form = null;
                ready.Set();
            }
        }) { IsBackground = true, Name = "Sempulse cat" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void SetMode(string next)
    {
        mode = next;
        Send("mode " + next);
    }

    public void SetHidden(bool value)
    {
        hidden = value;
        Send(value ? "hide" : "show");
    }

    void Send(string command)
    {
        var target = form;
        if (target is null || target.IsDisposed) return;
        try { target.BeginInvoke(() => target.HandleCommand(command)); }
        catch (InvalidOperationException) { /* Window is closing. */ }
    }

    public void Stop()
    {
        var target = form;
        if (target is not null && !target.IsDisposed)
        {
            try { target.BeginInvoke(target.Close); }
            catch (InvalidOperationException) { }
        }
        thread?.Join(TimeSpan.FromSeconds(2));
        thread = null;
        form = null;
    }

    public void Dispose() => Stop();
}
