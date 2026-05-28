namespace Domain_Guardian.Properties {

    internal sealed partial class Settings {

        public Settings()
        {
        }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("True")]
        public bool TestDnsCheck
        {
            get { return (bool)(this["TestDnsCheck"]); }
            set { this["TestDnsCheck"] = value; }
        }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("True")]
        public bool TestReplication
        {
            get { return (bool)(this["TestReplication"]); }
            set { this["TestReplication"] = value; }
        }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("True")]
        public bool TestTimeSkew
        {
            get { return (bool)(this["TestTimeSkew"]); }
            set { this["TestTimeSkew"] = value; }
        }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("True")]
        public bool TestLdapBind
        {
            get { return (bool)(this["TestLdapBind"]); }
            set { this["TestLdapBind"] = value; }
        }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("True")]
        public bool TestCertDhcp
        {
            get { return (bool)(this["TestCertDhcp"]); }
            set { this["TestCertDhcp"] = value; }
        }

        [global::System.Configuration.UserScopedSettingAttribute()]
        [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("True")]
        public bool TestSmbLdapSigning
        {
            get { return (bool)(this["TestSmbLdapSigning"]); }
            set { this["TestSmbLdapSigning"] = value; }
        }
    }
}
