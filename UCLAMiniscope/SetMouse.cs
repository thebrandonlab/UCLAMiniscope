using System;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using Bonsai;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Sets mouse information (ID and root path) in the shared MouseInfoService for use by other nodes.
    /// </summary>
    [Description("Sets mouse information to MouseInfoService for other nodes to use.")]
    public class SetMouse : Source<Unit>
    {
        /// <summary>
        /// Gets or sets the mouse ID for the recording session.
        /// </summary>
        [Description("Sets the mouse ID for the recording.")]
        public string MouseID { get; set; } = "Mouse01";

        /// <summary>
        /// Gets or sets the root directory path where experiment recordings will be saved.
        /// </summary>
        [Description("Sets the root path for the experiment recordings.")]
        [Editor("Bonsai.Design.FolderNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string RootPath { get; set; } = "C:/MiniscopeRecordings";

        /// <summary>
        /// Generates a single Unit value after setting the mouse information in the service.
        /// </summary>
        /// <returns>An observable sequence containing a single Unit value.</returns>
        public override IObservable<Unit> Generate()
        {
            MouseInfoService.MouseID = MouseID;
            MouseInfoService.RootPath = RootPath;

            // Emits a single value to indicate the operation is complete
            return Observable.Return(Unit.Default);
        }
    }
}
