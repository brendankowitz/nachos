/*
The general form of dbo.JsonNumberOrderKey, for number text longer than 4000 characters: the same algorithm on
VARCHAR (MAX) values. dbo.JsonNumberOrderKey documents the key. The bodies of the two functions are identical apart from
VARCHAR (MAX) / VARCHAR (8000) and the delegation at the top of dbo.JsonNumberOrderKey (SqlNumberOrderKeyTests pins this).
*/
CREATE FUNCTION [dbo].[JsonNumberOrderKeyLong] (@number NVARCHAR (MAX))
RETURNS VARCHAR (MAX)
WITH SCHEMABINDING, RETURNS NULL ON NULL INPUT, INLINE = OFF
AS
BEGIN    -- Not number-shaped (-?D(.D)?([eE][+-]?D)? with D one or more digits): NULL, never an error.
    -- Characters are checked on the NVARCHAR input under a binary collation first: the CAST below would map fullwidth
    -- and other non-ASCII digits (and superscripts) to ASCII ones under a linguistic collation.
    IF PATINDEX(N'%[^-+.0-9eE]%', @number COLLATE Latin1_General_100_BIN2) > 0
        RETURN NULL;
    DECLARE @text VARCHAR (MAX) = CAST(@number AS VARCHAR (MAX));
    DECLARE @negative BIT = CASE WHEN LEFT(@text, 1) = '-' THEN 1 ELSE 0 END;
    IF @negative = 1
        SET @text = STUFF(@text, 1, 1, '');

    -- Split into the significand I.F and the exponent X.
    DECLARE @at BIGINT = PATINDEX('%[eE]%', @text);
    DECLARE @significand VARCHAR (MAX) = CASE WHEN @at > 0 THEN LEFT(@text, @at - 1) ELSE @text END;
    DECLARE @exponent VARCHAR (MAX) = CASE WHEN @at > 0 THEN STUFF(@text, 1, @at, '') ELSE '0' END;
    DECLARE @exponentNegative BIT = CASE WHEN LEFT(@exponent, 1) = '-' THEN 1 ELSE 0 END;
    IF LEFT(@exponent, 1) IN ('-', '+')
        SET @exponent = STUFF(@exponent, 1, 1, '');
    DECLARE @dot BIGINT = CHARINDEX('.', @significand);
    IF @significand = '' OR @significand LIKE '%[^0-9.]%' OR LEFT(@significand, 1) = '.' OR RIGHT(@significand, 1) = '.'
       OR CHARINDEX('.', @significand, @dot + 1) > 0 OR @exponent = '' OR @exponent LIKE '%[^0-9]%'
        RETURN NULL;

    DECLARE @exponentStart BIGINT = PATINDEX('%[^0]%', @exponent);
    IF @exponentStart = 0
        SELECT @exponent = '0', @exponentNegative = 0;
    ELSE
        SET @exponent = STUFF(@exponent, 1, @exponentStart - 1, '');

    -- The mantissa: the significant digits of I.F; the value is zero when there are none.
    DECLARE @integerLength BIGINT = CASE WHEN @dot > 0 THEN @dot - 1 ELSE LEN(@significand) END;
    DECLARE @digits VARCHAR (MAX) = REPLACE(@significand, '.', '');
    DECLARE @first BIGINT = PATINDEX('%[^0]%', @digits);
    IF @first = 0
        RETURN '1';
    DECLARE @mantissa VARCHAR (MAX) =
        SUBSTRING(@digits, @first, LEN(@digits) - PATINDEX('%[^0]%', REVERSE(@digits)) - @first + 2);

    -- a = (+/-)X + shift, exactly. The shift is bounded by the text's length; X is not.
    DECLARE @shift BIGINT = @integerLength - @first;
    DECLARE @aNegative BIT, @aDigits VARCHAR (MAX);
    IF LEN(@exponent) <= 18
    BEGIN
        DECLARE @a BIGINT = CAST(@exponent AS BIGINT) * (1 - 2 * @exponentNegative) + @shift;
        SELECT @aNegative = CASE WHEN @a < 0 THEN 1 ELSE 0 END, @aDigits = CAST(ABS(@a) AS VARCHAR (20));
    END
    ELSE
    BEGIN
        -- |X| >= 10^18 exceeds any shift, so a has X's sign; add the shift to |X| in 18-digit chunks, right to left.
        DECLARE @carry BIGINT = @shift * (1 - 2 * @exponentNegative);
        DECLARE @rest VARCHAR (MAX) = @exponent, @result VARCHAR (MAX) = '', @chunk BIGINT;
        WHILE LEN(@rest) > 0 OR @carry <> 0
        BEGIN
            SET @chunk = CASE WHEN LEN(@rest) > 0 THEN CAST(RIGHT(@rest, 18) AS BIGINT) ELSE 0 END + @carry;
            SET @rest = CASE WHEN LEN(@rest) > 18 THEN LEFT(@rest, LEN(@rest) - 18) ELSE '' END;
            SET @carry = CASE WHEN @chunk >= 1000000000000000000 THEN 1 WHEN @chunk < 0 THEN -1 ELSE 0 END;
            SET @result = RIGHT('000000000000000000' + CAST(@chunk - @carry * 1000000000000000000 AS VARCHAR (19)), 18) + @result;
            IF @carry = 0
                SELECT @result = @rest + @result, @rest = '';
        END

        SELECT @aNegative = @exponentNegative, @aDigits = STUFF(@result, 1, PATINDEX('%[^0]%', @result) - 1, '');
    END

    DECLARE @field VARCHAR (MAX) = RIGHT('0000000000' + CAST(LEN(@aDigits) AS VARCHAR (19)), 10) + @aDigits;
    SET @field = CASE WHEN @aNegative = 0 THEN '1' + @field ELSE '0' + TRANSLATE(@field, '0123456789', '9876543210') END;
    RETURN CASE
        WHEN @negative = 0 THEN '2' + @field + @mantissa
        ELSE '0' + TRANSLATE(@field + @mantissa, '0123456789', '9876543210') + ':'
    END;
END