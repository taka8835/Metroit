﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Metroit.Api.Win32;
using Metroit.Windows.Forms.Extensions;

namespace Metroit.Windows.Forms
{
    /// <summary>
    /// 標準TextBoxを拡張し、新たにいくつかの機能を設けたテキストエリアを提供します。
    /// </summary>
    /// <remarks>
    /// [拡張機能]<br />
    /// 　・コピー、貼り付け、切り取り個別制御。<br />
    /// 　・TextChangeValidation イベントの実装による入力値の可否制御。<br />
    /// 　・ラベルによる代替表示。<br />
    /// 　・最大入力文字後、次コントロールへの自動遷移(ペースト時、コードによる直接入力は行わない)。<br />
    /// 　・フォーカス取得時の全選択。<br />
    /// 　・フォーカス取得時の背景色、文字色の変更。<br />
    /// 　・Multiline時、Ctrl+Aによるテキストの全選択。<br />
    /// 　・部分一致可能な独自のオートコンプリート機能。<br />
    /// <br />
    /// TextChangeValidation イベントは、削除／切り取り／元に戻すなど、一度入力が許容された値に対しての操作や、オートコンプリートの候補値による値設定では発生しません。<br />
    /// そのため、TextChangeValidation イベント内で、状態変更・保持を行うべきではありません。<br />
    /// 状態変更・保持などを行う場合はTextChanged イベント、またはValidated イベントで行うべきです。<br />
    /// TextChangeValidation イベントが発生しない操作<br />
    /// 　・AutoCompleteMode プロパティによって、入力値の補完が行われた時<br />
    /// 　・BackSpace、Deleteキーなどによって、入力値が削除された時<br />
    /// 　・切り取り、Ctrl+Xによって、入力値が切り取られた時<br />
    /// 　・元に戻す、Ctrl+Zによって、入力値が直前の内容に戻された時<br />
    /// </remarks>
    /// <exception cref="Metroit.Windows.Forms.DeniedTextException">
    /// Textプロパティをコード上で書き換え、入力値が拒否された場合に発生します。
    /// </exception>
    [ToolboxItem(true)]
    public class MetTextBox : TextBox, ISupportInitialize, IControlRollback, IOuterFrame
    {
        /// <summary>
        /// Keys プロパティエディタにEnterなどが含まれないため、全キーを含めるためにKeysを生成し直します。
        /// </summary>
        static MetTextBox()
        {
            // ShortcutKeysEditor に Enter, Back, Home を追加する。
            ShortcutKeysEditorHack.AddKeys(new Keys[] { Keys.Enter, Keys.Back, Keys.Home }, true);
        }

        /// <summary>
        /// MetTextBox の新しいインスタンスを初期化します。
        /// </summary>
        public MetTextBox()
            : base()
        {
            // プロパティのデフォルト値の設定
            this.CustomAutoCompleteKeys = this.defaultCustomAutoCompleteKeys;

            // デザイン時は制御しない
            if (this.IsDesignMode())
            {
                return;
            }

            // イベントハンドラの追加
            this.Enter += MetTextBox_Enter;
            this.Leave += MetTextBox_Leave;
            this.KeyDown += MetTextBox_KeyDown;
            this.KeyPress += MetTextBox_KeyPress;
            this.KeyUp += MetTextBox_KeyUp;
            this.MouseDown += MetTextBox_MouseDown;
        }

        /// <summary>
        /// MetTextBox のインスタンスを破棄します。
        /// </summary>
        ~MetTextBox()
        {
            this.Dispose(true);
        }

        #region イベント

        // マウスクリックによるフォーカス取得かどうか
        private bool isMouseClickFocus = false;

        // IME入力時、何か1文字でも入力値を拒否したかどうか
        private bool isDenyImeKeyChar = false;

        private string enterText = "";

        // 候補コンボボックスからリストを選択したかどうか
        private bool candidateSelectedValueChanging = false;

        // 候補コンボボックスが表示されている状態で、テキスト値の変更をかけているかどうか
        private bool candidateTextChanging = false;

        /// <summary>
        /// フォーカスを得た時、色の変更とテキストの反転を行う。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MetTextBox_Enter(object sender, EventArgs e)
        {
            this.enterText = this.Text;

            // フォーカス取得時の色に変更
            this.changeFocusColor();

            // テキストの反転
            // マウスクリック時は、MouseDownイベントが走行したタイミングで選択が解除されてしまうため、MouseDownイベントで実施する
            if (this.FocusSelect && MouseButtons == MouseButtons.None)
            {
                this.SelectAll();
            }
            if (this.FocusSelect && MouseButtons != MouseButtons.None)
            {
                this.isMouseClickFocus = true;
            }
        }

        /// <summary>
        /// フォーカスを失った時、色の変更を行う。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MetTextBox_Leave(object sender, EventArgs e)
        {
            this.changeBaseColor();

            // リストから上下で選択後、Tabで遷移した時、リストを閉じる
            if (this.CustomAutoCompleteBox.IsOpen && !this.CustomAutoCompleteBox.IsActive)
            {
                this.CustomAutoCompleteBox.Close();
            }
        }

        /// <summary>
        /// キーが離れた時の動作
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MetTextBox_KeyUp(object sender, KeyEventArgs e)
        {
            // 入力文字確定時は入力文字拒否フラグは初期化する
            this.isDenyImeKeyChar = false;
        }

        /// <summary>
        /// キーが押された時の動作
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MetTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Multiline時のCtrl+A
            if (this.Multiline && this.MultilineSelectAll && e.KeyData == (Keys.Control | Keys.A))
            {
                this.SelectAll();
                e.SuppressKeyPress = true;
                return;
            }

            // カスタムオートコンプリートを利用する場合
            if (this.CustomAutoCompleteMode)
            {
                // 指定されたキーが押下された時、候補コンボボックスを表示する
                var matchKey = CustomAutoCompleteKeys.FirstOrDefault((value) => value == e.KeyData);
                if (matchKey == Keys.None)
                {
                    return;
                }
                // 読み取り専用や操作不要な時は動作させない
                if (this.ReadOnly || this.readOnlyLabel || !this.Enabled)
                {
                    return;
                }
                if (this.CustomAutoCompleteBox.IsOpen)
                {
                    // 既に表示済みの場合は閉じる
                    this.CustomAutoCompleteBox.Close();
                    e.SuppressKeyPress = true;
                }
                else
                {
                    // 候補コンボボックスを表示する
                    //this.CustomAutoCompleteBox.Extract(this.Text);
                    this.CustomAutoCompleteBox.Open();
                    e.SuppressKeyPress = true;
                }
            }
        }

        /// <summary>
        /// キーが押された時の動作
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MetTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            // 文字キー押下時、TextChangingを走行させる。
            // BackSpace, Ctrl+X, Ctrl+Z, Ctrl+Vは何もしない
            if (e.KeyChar == '\b' || e.KeyChar == '\u0018' || e.KeyChar == '\u001a' || e.KeyChar == '\u0016')
            {
                return;
            }

            // Multiline でない時にEnter, Ctrl+Enter, Shift+Enter押下時は何もしない
            if (!this.Multiline && (e.KeyChar == '\r' || e.KeyChar == '\n'))
            {
                return;
            }

            // IME入力時、WM_CHAR, WM_IME_ENDCOMPOSITIONあたりでそれぞれ1文字ずつKeyPressが走行する
            // いずれかの文字が拒否されたら無条件に全文字を拒否する
            if (this.isDenyImeKeyChar)
            {
                e.Handled = true;
                return;
            }

            var inputText = this.createInputString(e.KeyChar.ToString());

            // 入力後のテキストを取得
            var afterText = this.createTextAfterInput(e);

            // TextChangingイベントの発行
            var args = new TextChangeValidationEventArgs();
            args.Cancel = false;
            args.Before = base.Text;
            args.Input = inputText;
            args.After = afterText;
            this.OnTextChangeValidation(args);

            // キャンセルされたら入力を拒否する
            if (args.Cancel)
            {
                this.isDenyImeKeyChar = true;
                e.Handled = true;
            }
        }

        /// <summary>
        /// マウスがクリックされてフォーカスを得た時、テキストの反転を行う。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MetTextBox_MouseDown(object sender, MouseEventArgs e)
        {
            // テキストの反転
            if (this.FocusSelect && this.isMouseClickFocus)
            {
                this.SelectAll();
                this.isMouseClickFocus = false;
            }
        }

        /// <summary>
        /// 候補コンボボックスから候補を選択した時、候補の値をテキストに表示する。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CandidateBox_SelectedValueChanged(object sender, EventArgs e)
        {
            // データソースの設定中は何もしない
            if (this.CustomAutoCompleteBox.DataSourceChanging)
            {
                return;
            }

            // プルダウン表示中は何もしない
            if (this.CustomAutoCompleteBox.BoxOpening)
            {
                return;
            }

            // テキストの直接入力によって候補が決定された(TextChanged)場合、処理しない
            if (candidateTextChanging)
            {
                return;
            }

            // 選択された候補の値をテキストに表示する
            candidateSelectedValueChanging = true;
            this.Text = this.CustomAutoCompleteBox.CandidateBox.Text;
            this.SelectionLength = this.Text.Length;
            candidateSelectedValueChanging = false;
        }

        /// <summary>
        /// 文字キーによってテキスト入力が行われた際の、入力後テキストを取得する。
        /// </summary>
        /// <param name="e">キーイベント</param>
        /// <returns>入力後テキスト</returns>
        private string createTextAfterInput(KeyPressEventArgs e)
        {
            var headText = (this.SelectionStart == 0 ? "" : base.Text.Substring(0, SelectionStart));
            var footText = base.Text.Substring(SelectionStart + SelectionLength);

            // Enterが押されたらCRLFに変換
            var inputText = this.createInputString(e.KeyChar.ToString());

            if (e.KeyChar == '\r' || e.KeyChar == '\n')
            {
                inputText = Environment.NewLine;
            }
            return headText + inputText + footText;
        }

        /// <summary>
        /// オートフォーカスを行う。
        /// </summary>
        private void autoFocus()
        {
            if (this.AutoFocus && this.Focused && this.CanAutoFocus())
            {
                this.MoveNextControl();
            }
        }

        /// <summary>
        /// オートフォーカスが実行可能な状態かどうかを取得します。
        /// </summary>
        /// <returns>true:実行可能, false:実行不可能</returns>
        protected virtual bool CanAutoFocus()
        {
            if (this.MaxLength != this.Text.Length)
            {
                return false;
            }

            return true;
        }

        #endregion

        #region 追加イベント

        /// <summary>
        /// <para>Text プロパティの値が追加／挿入されているときに発生するイベントです。値を検証し、変更を拒否することができます。</para>
        /// <para>値の削除や元に戻す操作では発生しないことに注意してください。</para>
        /// </summary>
        [MetCategory("MetPropertyChange")]
        [MetDescription("MetTextBoxTextChangeValidationEventHandler")]
        public event TextChangeValidationEventHandler TextChangeValidation;

        /// <summary>
        /// 入力値を変更しているときに走行します。
        /// </summary>
        /// <param name="e">入力値許可イベントオブジェクト。</param>
        protected virtual void OnTextChangeValidation(TextChangeValidationEventArgs e)
        {
            if (this.TextChangeValidation != null)
            {
                this.TextChangeValidation(this, e);
            }
        }

        #endregion

        #region プロパティ

        private bool visible = true;

        /// <summary>
        /// コントロールがバインドされるコンテナーの端を取得または設定し、親のサイズ変更時に、コントロールのサイズがどのように変化するかを決定します。
        /// </summary>
        public override AnchorStyles Anchor
        {
            get => base.Anchor;
            set
            {
                base.Anchor = value;
                if (this.initialized && this.ReadOnlyLabel)
                {
                    this.label.Anchor = this.Anchor;
                }
            }
        }

        /// <summary>
        /// コントロールとそのすべての子コントロールが表示されているかどうかを示す値を取得または設定します。
        /// </summary>
        [Browsable(true)]
        [DefaultValue(true)]
        public new bool Visible
        {
            get => this.visible;
            set
            {
                this.visible = value;
                if (this.IsDesignMode())
                {
                    return;
                }
                if (!this.initialized)
                {
                    return;
                }
                this.switchLabel();
            }
        }

        /// <summary>
        /// コントロールに関連付けられたテキストです。
        /// </summary>
        public override string Text
        {
            get => base.Text;
            set
            {
                // 設定値に変更がない場合は処理しない
                if (this.Text == value)
                {
                    return;
                }

                // 入力値チェックイベント
                var inputText = this.createInputString(value);
                var e = new TextChangeValidationEventArgs();
                e.Cancel = false;
                e.Before = base.Text;
                e.Input = inputText;
                e.After = inputText;
                this.OnTextChangeValidation(e);
                if (e.Cancel)
                {
                    throw new DeniedTextException(
                            string.Format(ExceptionResources.GetString("InvalidValue"), inputText), inputText);
                }

                base.Text = value;

                if (this.IsDesignMode())
                {
                    return;
                }
                if (this.ReadOnlyLabel)
                {
                    this.label.Text = base.Text;
                }
            }
        }

        /// <summary>
        /// 取得またはテキスト ボックスにテキストが読み取り専用かどうかを示す値を設定します。
        /// </summary>
        public new bool ReadOnly
        {
            get => base.ReadOnly;
            set
            {
                if (value && this.ReadOnlyLabel)
                {
                    this.ReadOnlyLabel = false;
                }
                base.ReadOnly = value;

                // 背景色・文字色の変更
                this.ChangeDisplayColor();
            }
        }

        // FIXED: 例えば特定条件下で色を変えるなどのユーザー実装が行えなくなるため、コード実装は可能とする。
        /// <summary>
        /// コントロールの前景色を取得または特定のプロパティによって強制的に前景色を設定します。
        /// 意図的に前景色の変更が必要な場合のみ利用してください。
        /// </summary>
        [Browsable(false)]
        [ReadOnly(true)]
        //[EditorBrowsable(EditorBrowsableState.Never)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new Color ForeColor
        {
            get => base.ForeColor;
            set => base.ForeColor = value;
        }

        /// <summary>
        /// コントロールの背景色を取得または特定のプロパティによって強制的に背景色を設定します。
        /// 意図的に背景色の変更が必要な場合のみ利用してください。
        /// </summary>
        [Browsable(false)]
        [ReadOnly(true)]
        //[EditorBrowsable(EditorBrowsableState.Never)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new Color BackColor
        {
            get => base.BackColor;
            set => base.ForeColor = value;
        }

        #endregion

        #region 追加プロパティ

        private bool initialized = false;
        private Color baseForeColor = SystemColors.WindowText;
        private Color defaultBaseBackColor => this.ReadOnly ? SystemColors.Control : SystemColors.Window;   // BaseBackColor の既定値
        private Color? baseBackColor = null;
        private Color focusForeColor = SystemColors.WindowText;
        private Color defaultFocusBackColor => this.ReadOnly ? SystemColors.Control : SystemColors.Window;  // FocusBackColor の既定値
        private Color? focusBackColor = null;
        private bool readOnlyLabel = false;
        private Label label = null;
        private bool customAutoCompleteMode = false;
        private Keys[] defaultCustomAutoCompleteKeys = new Keys[] {
                Keys.Alt | Keys.Up,
                Keys.Alt | Keys.Down,
                Keys.F4,
                Keys.Control | Keys.F4,
                Keys.Shift | Keys.F4
        };

        /// <summary>
        /// InitializeComponent() によってコントロールの準備が完了したかどうかを取得します。
        /// </summary>
        protected bool Initialized { get => this.initialized; }

        /// <summary>
        /// Label による代替表示を行うかどうかを取得または設定します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetAppearance")]
        [DefaultValue(false)]
        [MetDescription("ControlReadOnlyLabel")]
        public bool ReadOnlyLabel
        {
            get
            {
                return this.readOnlyLabel;
            }
            set
            {
                if (value && this.ReadOnly)
                {
                    this.ReadOnly = false;
                }
                this.readOnlyLabel = value;

                if (this.IsDesignMode())
                {
                    return;
                }
                if (!this.initialized)
                {
                    return;
                }

                this.switchLabel();
            }
        }

        /// <summary>
        /// コントロールの前景色を取得または設定します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetAppearance")]
        [DefaultValue(typeof(Color), "WindowText")]
        [MetDescription("ControlBaseForeColor")]
        public Color BaseForeColor
        {
            get { return this.baseForeColor; }
            set
            {
                this.baseForeColor = value;

                // 背景色・文字色の変更
                this.ChangeDisplayColor();
            }
        }

        /// <summary>
        /// コントロールの背景色を取得または設定します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetAppearance")]
        [MetDescription("ControlBaseBackColor")]
        public Color BaseBackColor
        {
            get
            {
                this.Refresh();
                return this.baseBackColor ?? this.defaultBaseBackColor;
            }
            set
            {
                this.baseBackColor = value;

                // 背景色・文字色の変更
                this.ChangeDisplayColor();
            }
        }

        /// <summary>
        /// BaseBackColor が既定値から変更されたかどうかを返却する。
        /// </summary>
        /// <returns>true:変更された, false:変更されていない</returns>
        private bool ShouldSerializeBaseBackColor() => this.baseBackColor != null && this.baseBackColor != this.defaultBaseBackColor;

        /// <summary>
        /// BaseBackColor のリセット操作を行う。
        /// </summary>
        private void ResetBaseBackColor() => this.baseBackColor = null;

        /// <summary>
        /// フォーカス時のコントロールの前景色を取得または設定します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetAppearance")]
        [DefaultValue(typeof(Color), "WindowText")]
        [MetDescription("ControlFocusForeColor")]
        public Color FocusForeColor
        {
            get { return this.focusForeColor; }
            set
            {
                this.focusForeColor = value;

                // 背景色・文字色の変更
                this.ChangeDisplayColor();
            }

        }

        /// <summary>
        /// フォーカス時のコントロールの背景色を取得または設定します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetAppearance")]
        [MetDescription("ControlFocusBackColor")]
        public Color FocusBackColor
        {
            get
            {
                this.Refresh();
                return this.focusBackColor ?? this.defaultFocusBackColor;
            }
            set
            {
                this.focusBackColor = value;

                // 背景色・文字色の変更
                this.ChangeDisplayColor();
            }
        }

        /// <summary>
        /// FocusBackColor が既定値から変更されたかどうかを返却する。
        /// </summary>
        /// <returns>true:変更された, false:変更されていない</returns>
        private bool ShouldSerializeFocusBackColor() => this.focusBackColor != null && this.focusBackColor != this.defaultFocusBackColor;

        /// <summary>
        /// FocusBackColor のリセット操作を行う。
        /// </summary>
        private void ResetFocusBackColor() => this.focusBackColor = null;

        /// <summary>
        /// フォーカスを得た時、テキストを反転させるかを設定します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetBehavior")]
        [DefaultValue(false)]
        [MetDescription("ControlFocusSelect")]
        public bool FocusSelect { get; set; } = false;

        /// <summary>
        /// 複数行テキストエディットの時、Ctrl+Aを押すことでテキストを全選択するかを設定します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetBehavior")]
        [DefaultValue(false)]
        [MetDescription("MetTextBoxMultilineSelectAll")]
        public bool MultilineSelectAll { get; set; } = false;

        /// <summary>
        /// 最大入力まで行われた時、次のコントロールへフォーカスを遷移するかを設定します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetBehavior")]
        [DefaultValue(false)]
        [MetDescription("ControlAutoFocus")]
        public bool AutoFocus { get; set; } = false;

        /// <summary>
        /// カスタムオートコンプリートを利用するか設定します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetOther")]
        [MetDescription("MetTextBoxCustomAutoCompleteMode")]
        [DefaultValue(false)]
        public bool CustomAutoCompleteMode
        {
            get
            {
                return this.customAutoCompleteMode;
            }
            set
            {
                if (value)
                {
                    this.AutoCompleteMode = AutoCompleteMode.None;
                }
                this.customAutoCompleteMode = value;
            }
        }

        /// <summary>
        /// オートコンプリート候補プルダウンを表示するキーを設定します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetOther")]
        [MetDescription("MetTextBoxCustomAutoCompleteDisplayKeys")]
        public Keys[] CustomAutoCompleteKeys { get; set; }

        /// <summary>
        /// CustomAutoCompleteKeys が既定値から変更されたかどうかを返却する。
        /// </summary>
        /// <returns>true:変更された, false:変更されていない</returns>
        private bool ShouldSerializeCustomAutoCompleteKeys() => this.CustomAutoCompleteKeys != this.defaultCustomAutoCompleteKeys;

        /// <summary>
        /// CustomAutoCompleteKeys のリセット操作を行う。
        /// </summary>
        private void ResetCustomAutoCompleteKeys() => this.CustomAutoCompleteKeys = this.defaultCustomAutoCompleteKeys;

        /// <summary>
        /// カスタムオートコンプリート情報を設定または取得します。
        /// </summary>
        [Browsable(true)]
        [MetCategory("MetOther")]
        [MetDescription("MetTextBoxCustomAutoCompleteBox")]
        public AutoCompleteBox CustomAutoCompleteBox { get; set; } = new AutoCompleteBox();

        /// <summary>
        /// 表示色をフォーカス色に変更する。
        /// </summary>
        private void changeFocusColor()
        {
            base.ForeColor = this.FocusForeColor;
            base.BackColor = this.FocusBackColor;
        }

        /// <summary>
        /// 表示色を非フォーカス色に変更する。
        /// </summary>
        private void changeBaseColor()
        {
            base.ForeColor = this.BaseForeColor;
            base.BackColor = this.BaseBackColor;

            // ラベルの代替表示を行っている場合はラベルの表示色も変更
            if (this.ReadOnlyLabel)
            {
                this.label.ForeColor = this.BaseForeColor;
                this.label.BackColor = this.BaseBackColor;
            }
        }

        /// <summary>
        /// コントロールのアクティブ状態に応じた表示色に変更する。
        /// </summary>
        protected virtual void ChangeDisplayColor()
        {
            // アクティブの時にアクティブ色に変更
            var form = this.FindForm();
            if (form != null && form.ActiveControl == this)
            {
                this.changeFocusColor();
            }
            else
            {
                this.changeBaseColor();
            }
        }

        /// <summary>
        /// 表示状態に応じて、Visible、ラベル代替を切り替える。
        /// </summary>
        private void switchLabel()
        {
            // 非表示
            if (!this.Visible)
            {
                // ラベル代替なしの場合は通常のVisible
                if (!this.ReadOnlyLabel)
                {
                    base.Visible = this.Visible;
                    return;
                }

                // ラベルを一旦削除する
                if (this.label != null)
                {
                    this.label.Parent.Controls.Remove(this.label);
                    this.label.Dispose();
                    this.label = null;
                }
                base.Visible = this.Visible;
                return;
            }

            // 表示でラベル代替なしの場合はラベルを消す
            if (!this.ReadOnlyLabel)
            {
                if (this.label != null)
                {
                    this.label.Parent.Controls.Remove(this.label);
                    this.label.Dispose();
                    this.label = null;
                }
                base.Visible = this.Visible;
                return;
            }

            // 表示でラベル代替あり
            this.label = new Label();
            this.label.Text = this.Text;
            this.label.Size = this.Size;
            this.label.Location = this.Location;
            this.label.Margin = this.Margin;
            this.label.MaximumSize = this.MaximumSize;
            this.label.MinimumSize = this.MinimumSize;
            this.label.Padding = this.DefaultPadding;
            this.label.Visible = this.Visible;
            this.label.Enabled = this.Enabled;
            this.label.Dock = this.Dock;
            this.label.BackColor = this.BaseBackColor;
            this.label.ForeColor = this.BaseForeColor;
            this.label.Font = this.Font;
            this.label.Anchor = this.Anchor;
            this.label.RightToLeft = this.RightToLeft;
            this.label.BorderStyle = this.BorderStyle;
            switch (this.TextAlign)
            {
                case HorizontalAlignment.Center:
                    this.label.TextAlign = Multiline ?
                            ContentAlignment.TopCenter : ContentAlignment.MiddleCenter;
                    break;

                case HorizontalAlignment.Left:
                    this.label.TextAlign = Multiline ?
                            ContentAlignment.TopLeft : ContentAlignment.MiddleLeft;
                    break;

                case HorizontalAlignment.Right:
                    this.label.TextAlign = Multiline ?
                            ContentAlignment.TopRight : ContentAlignment.MiddleRight;
                    break;
            }

            base.Visible = false;

            if (this.Parent.GetType() == typeof(TableLayoutPanel))
            {
                var tlp = (TableLayoutPanel)this.Parent;
                var position = tlp.GetPositionFromControl(this);
                tlp.Controls.Add(this.label, position.Column, position.Row);
            }

            else if (this.Parent.GetType() == typeof(FlowLayoutPanel))
            {
                var flp = (FlowLayoutPanel)this.Parent;
                flp.Controls.Add(this.label);
                flp.Controls.SetChildIndex(this.label, flp.Controls.IndexOf(this));
            }
            else
            {
                this.Parent.Controls.Add(this.label);
            }
        }


        #region コントロールの外枠の色

        /// <summary>
        /// コントロールの枠色を取得または設定します。
        /// </summary>
        [Browsable(true)]
        [DefaultValue(typeof(Color), "Transparent")]
        [MetCategory("MetAppearance")]
        [MetDescription("ControlBaseOuterFrameColor")]
        public Color BaseOuterFrameColor { get; set; } = Color.Transparent;

        /// <summary>
        /// フォーカス時のコントロールの枠色を取得または設定します。
        /// </summary>
        [Browsable(true)]
        [DefaultValue(typeof(Color), "Transparent")]
        [MetCategory("MetAppearance")]
        [MetDescription("ControlFocusOuterFrameColor")]
        public Color FocusOuterFrameColor { get; set; } = Color.Transparent;

        /// <summary>
        /// エラー時のコントロールの枠色を取得または設定します。
        /// </summary>
        [Browsable(true)]
        [DefaultValue(typeof(Color), "Red")]
        [MetCategory("MetAppearance")]
        [MetDescription("ControlErrorOuterFrameColor")]
        public Color ErrorOuterFrameColor { get; set; } = Color.Red;

        private bool error = false;

        /// <summary>
        /// コントロールがエラーかどうかを取得または設定します。
        /// </summary>
        [Browsable(true)]
        [DefaultValue(false)]
        [MetCategory("MetAppearance")]
        [MetDescription("ControlError")]
        public bool Error
        {
            get => this.error;
            set
            {
                this.error = value;
                this.drawOuterFrame();
            }
        }

        #endregion

        #endregion

        #region メソッド

        /// <summary>
        /// InitializeComponent()でコントロールの初期化が完了していないことを通知します。
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void BeginInit()
        {
            this.initialized = false;
        }

        /// <summary>
        /// InitializeComponent()でコントロールの初期化が完了したことを通知し、ReadOnlyLabelの制御を行います。
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void EndInit()
        {
            if (this.IsDesignMode())
            {
                return;
            }

            this.initialized = true;

            // オートコンプリート対象を自身に設定
            this.CustomAutoCompleteBox.SetTarget(this);

            // オートコンプリートのイベントを設定
            this.CustomAutoCompleteBox.CandidateBox.SelectedValueChanged += CandidateBox_SelectedValueChanged;

            // ラベルへの切り替えを実施
            this.switchLabel();
        }

        /// <summary>
        /// ロールバック済みかどうかを取得します。
        /// </summary>
        /// <param name="sender">ロールバック指示オブジェクト。</param>
        /// <param name="control">ロールバック対象オブジェクト。</param>
        /// <returns>true:ロールバック済み, false:未ロールバック。</returns>
        public virtual bool IsRollbacked(object sender, Control control)
        {
            return this.Text == this.enterText;
        }

        /// <summary>
        /// フォーカスを得た時の値にロールバックを行います。
        /// </summary>
        /// <param name="sender">ロールバック指示オブジェクト。</param>
        /// <param name="control">ロールバック対象オブジェクト。</param>
        public virtual void Rollback(object sender, Control control)
        {
            this.Text = this.enterText;
        }

        /// <summary>
        /// 低レベルフォーカスを得た時の動作。
        /// </summary>
        /// <param name="e"></param>
        protected override void OnGotFocus(EventArgs e)
        {
            // リストをクリックして候補が決まった時、イベント走行させない
            if (this.CustomAutoCompleteBox.BoxClosing)
            {
                return;
            }
            base.OnGotFocus(e);
        }

        /// <summary>
        /// フォーカスを得た時の動作。
        /// </summary>
        /// <param name="e"></param>
        protected override void OnEnter(EventArgs e)
        {
            // リストをクリックして候補が決まった時、Onメソッド、イベント走行させない
            if (this.CustomAutoCompleteBox.BoxClosing)
            {
                // フォーカス色だけは変更する
                this.changeFocusColor();
                return;
            }
            base.OnEnter(e);
        }

        /// <summary>
        /// テキストが変更後、可能の場合にフォーカス遷移を行います。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);

            // オートコンプリート指定がある場合はオートフォーカスしない
            if (this.AutoCompleteMode != AutoCompleteMode.None)
            {
                return;
            }

            // カスタムオートコンプリートを利用する場合
            if (this.customAutoCompleteMode)
            {
                // 候補コンボボックスの上下選択によって候補が決定された(SelectedValueChanged)場合、処理しない
                if (candidateSelectedValueChanging)
                {
                    return;
                }

                if (this.CustomAutoCompleteBox.IsOpen)
                {
                    candidateTextChanging = true;

                    // 現在テキストで候補を絞り込み
                    this.CustomAutoCompleteBox.Extract(this.Text);

                    // 候補と合致する文字列がない場合は、候補の選択状態をリセットする
                    if (!this.CustomAutoCompleteBox.Contains(this.Text))
                    {
                        this.CustomAutoCompleteBox.CandidateBox.SelectedIndex = -1;
                    }
                    this.CustomAutoCompleteBox.CandidateBox.Text = this.Text;
                    candidateTextChanging = false;
                    return;
                }
                else
                {
                    this.CustomAutoCompleteBox.CandidateBox.Text = this.Text;
                }
            }

            // それ以外の場合はオートフォーカスする
            this.autoFocus();
        }

        /// <summary>
        /// コントロールの配置元が変更時、ラベル代替表示をしていたらラベルの配置元も変更します。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnParentChanged(EventArgs e)
        {
            if (this.initialized && this.ReadOnlyLabel)
            {
                this.label.Parent = this.Parent;
            }

            base.OnParentChanged(e);
        }

        /// <summary>
        /// コントロールの表示位置が変更時、ラベル代替表示をしていたらラベルの表示位置も変更します。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnLocationChanged(EventArgs e)
        {
            if (this.initialized && this.ReadOnlyLabel)
            {
                this.label.Location = this.Location;
            }
            base.OnLocationChanged(e);
        }

        /// <summary>
        /// コントロールのサイズが変更時、ラベル代替表示をしていたらラベルのサイズも変更します。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnSizeChanged(EventArgs e)
        {
            if (this.initialized && this.ReadOnlyLabel)
            {
                this.label.Size = this.Size;
            }
            base.OnSizeChanged(e);
        }

        /// <summary>
        /// コントロールのサイズが変更時、ラベル代替表示をしていたらラベルのサイズも変更します。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnResize(EventArgs e)
        {
            if (this.initialized && this.ReadOnlyLabel)
            {
                this.label.Size = this.Size;
            }
            base.OnResize(e);
        }

        /// <summary>
        /// コントロールのドッキングが変更時、ラベル代替表示をしていたらラベルのドッキングも変更します。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnDockChanged(EventArgs e)
        {
            if (this.initialized && this.ReadOnlyLabel)
            {
                this.label.Dock = this.Dock;
            }
            base.OnDockChanged(e);
        }

        /// <summary>
        /// コントロールの文字入力方向が変更時、ラベル代替表示をしていたらラベルの文字入力方向も変更します。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnRightToLeftChanged(EventArgs e)
        {
            if (this.initialized && this.ReadOnlyLabel)
            {
                this.label.RightToLeft = this.RightToLeft;
            }
            base.OnRightToLeftChanged(e);
        }

        /// <summary>
        /// コントロールのフォントが変更時、ラベル代替表示をしていたらラベルのフォントも変更します。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnFontChanged(EventArgs e)
        {
            if (this.initialized && this.ReadOnlyLabel)
            {
                this.label.Font = this.Font;
            }
            base.OnFontChanged(e);
        }

        /// <summary>
        /// コントロールのマージンが変更時、ラベル代替表示をしていたらラベルのマージンも変更します。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnMarginChanged(EventArgs e)
        {
            if (this.initialized && this.ReadOnlyLabel)
            {
                this.label.Margin = this.Margin;
            }
            base.OnMarginChanged(e);
        }

        /// <summary>
        /// コントロールのボーダーが変更時、ラベル代替表示をしていたらラベルのボーダーも変更します。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnBorderStyleChanged(EventArgs e)
        {
            if (this.initialized && this.ReadOnlyLabel)
            {
                this.label.BorderStyle = this.BorderStyle;
            }
            base.OnBorderStyleChanged(e);
        }

        /// <summary>
        /// コントロールの文字表示位置が変更時、ラベル代替表示をしていたらラベルの文字表示位置も変更します。
        /// </summary>
        /// <param name="e">イベントオブジェクト。</param>
        protected override void OnTextAlignChanged(EventArgs e)
        {
            if (this.initialized && this.ReadOnlyLabel)
            {
                switch (this.TextAlign)
                {
                    case HorizontalAlignment.Center:
                        this.label.TextAlign = Multiline ?
                                ContentAlignment.TopCenter : ContentAlignment.MiddleCenter;
                        break;

                    case HorizontalAlignment.Left:
                        this.label.TextAlign = Multiline ?
                                ContentAlignment.TopLeft : ContentAlignment.MiddleLeft;
                        break;

                    case HorizontalAlignment.Right:
                        this.label.TextAlign = Multiline ?
                                ContentAlignment.TopRight : ContentAlignment.MiddleRight;
                        break;
                }
            }
            base.OnTextAlignChanged(e);
        }

        /// <summary>
        /// ウィンドウメッセージを捕捉し、コンボボックスへの制御を行います。
        /// </summary>
        /// <param name="msg"></param>
        /// <param name="keyData"></param>
        /// <returns></returns>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // 候補コンボボックスが表示されている時の特定操作は、コンボボックスの制御を行う
            if (this.CustomAutoCompleteBox.IsOpen && msg.Msg == WindowMessage.WM_KEYDOWN)
            {
                // Escキーはリストを閉じる
                if (msg.WParam == new IntPtr(VirtualKey.VK_ESCAPE))
                {
                    this.CustomAutoCompleteBox.Close();
                    return true;
                }
                // 下キーはリストの選択肢を下へずらす
                if (msg.WParam == new IntPtr(VirtualKey.VK_DOWN))
                {
                    User32.SendMessage(this.CustomAutoCompleteBox.CandidateBox.Handle, WindowMessage.WM_KEYDOWN, VirtualKey.VK_DOWN, 0);
                    return true;
                }
                // 上キーはリストの選択肢を上へずらす
                if (msg.WParam == new IntPtr(VirtualKey.VK_UP))
                {
                    User32.SendMessage(this.CustomAutoCompleteBox.CandidateBox.Handle, WindowMessage.WM_KEYDOWN, VirtualKey.VK_UP, 0);
                    return true;
                }
                // Enterキーは選択肢を確定する
                if (msg.WParam == new IntPtr(VirtualKey.VK_RETURN))
                {
                    this.CustomAutoCompleteBox.Close();
                    return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// ウィンドウメッセージを捕捉し、コードによる値の設定および貼り付け時の制御を行います。
        /// </summary>
        /// <param name="m">ウィンドウメッセージ。</param>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WindowMessage.WM_PAINT)
            {
                this.drawOuterFrame();
            }

            // デザイン時は制御しない
            if (this.IsDesignMode())
            {
                base.WndProc(ref m);
                return;
            }

            // 貼り付けが拒否された場合は貼り付けを行わない
            if (m.Msg == WindowMessage.WM_PASTE && !this.canPaste())
            {
                return;
            }

            // 候補コンボボックスが開いており、マウスホイールが操作された時、マウスホイール操作を候補コンボボックスに伝える
            // (ProcessCmdKeyでは捕捉不可)
            if (this.CustomAutoCompleteBox.IsOpen && m.Msg == WindowMessage.WM_MOUSEWHEEL)
            {
                User32.SendMessage(this.CustomAutoCompleteBox.CandidateBox.Handle, m.Msg, m.WParam.ToInt32(), m.LParam.ToInt32());
                return;
            }

            base.WndProc(ref m);
        }

        /// <summary>
        /// コントロールの外枠の色を描画する。
        /// </summary>
        private void drawOuterFrame()
        {
            if (this.Parent == null)
            {
                return;
            }

            using (Graphics g = this.Parent.CreateGraphics())
            {
                // 外枠の変更
                var frameColor = this.BaseOuterFrameColor;
                var form = this.FindForm();
                if (form != null && form.ActiveControl == this)
                {
                    frameColor = this.FocusOuterFrameColor;
                }
                if (this.Error)
                {
                    frameColor = this.ErrorOuterFrameColor;
                }

                // ラベル読み取り専用時は親コントロールの背景色とする
                if (this.ReadOnlyLabel)
                {
                    frameColor = this.Parent.BackColor;
                }

                Rectangle rct = new Rectangle(this.Location, this.Size);
                rct.Inflate(1, 1);
                ControlPaint.DrawBorder(g, rct, frameColor, ButtonBorderStyle.Solid);
            }
        }

        /// <summary>
        /// 貼り付けが可能かどうかを取得する。
        /// </summary>
        /// <returns>true:貼付成功, false:貼付失敗</returns>
        private bool canPaste()
        {
            // 貼り付け可能なクリップボードが存在しない場合は処理しない
            var inputText = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(inputText))
            {
                return false;
            }

            // 貼り付け値の入力検証処理
            // マルチラインじゃない場合はクリップボードの1行目のみを処理する
            if (!Multiline)
            {
                int newLinePosition = inputText.IndexOf(Environment.NewLine);
                if (newLinePosition > -1)
                {
                    inputText = inputText.Substring(0, newLinePosition);
                }
            }

            inputText = this.createInputString(inputText);
            var afterText = this.createTextAfterPasted(inputText);
            var e = new TextChangeValidationEventArgs();
            e.Cancel = false;
            e.Before = base.Text;
            e.Input = inputText;
            e.After = afterText;
            this.OnTextChangeValidation(e);

            // 入力を拒否された場合は処理しない
            if (e.Cancel)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 入力文字を生成する。
        /// </summary>
        /// <param name="inputText">入力文字</param>
        /// <returns>生成後の入力文字</returns>
        private string createInputString(string inputText)
        {
            if (this.CharacterCasing == CharacterCasing.Lower)
            {
                return inputText.ToLower();
            }
            if (this.CharacterCasing == CharacterCasing.Upper)
            {
                return inputText.ToUpper();
            }

            return inputText;
        }

        /// <summary>
        /// ペースト操作によってテキストペーストが行われた後のテキストを取得する。
        /// </summary>
        /// <param name="pasteText">ペースト文字列</param>
        /// <returns>ペースト後テキスト</returns>
        private string createTextAfterPasted(string pasteText)
        {
            var afterText = new StringBuilder();

            afterText.Append((this.SelectionStart == 0 ? "" : base.Text.Substring(0, this.SelectionStart)));
            afterText.Append(pasteText);
            afterText.Append(base.Text.Substring(this.SelectionStart + this.SelectionLength));

            return afterText.ToString();
        }

        /// <summary>
        /// オブジェクトの破棄を行います。
        /// </summary>
        /// <param name="disposing">マネージリソースの破棄を行うかどうか。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (this.label != null)
                {
                    this.label.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        #endregion

    }

}
