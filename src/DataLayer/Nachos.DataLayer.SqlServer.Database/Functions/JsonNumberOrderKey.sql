/*
The order key of JSON number text, for exact numeric comparison in filters (FilterWriter in Nachos.DataLayer.SqlServer).
Two numbers compare exactly like their keys compared under a binary collation, at any size: there is no precision or
range limit, because no part of the number is ever converted to a fixed-width type it might not fit.

Input: the text of a JSON number (OPENJSON [value] where [type] = 2). NULL, or text that is not number-shaped, gives NULL.
Key:   zero (any spelling: 0, -0, 0.00, 0e5) is '1'. Otherwise the value is 0.m x 10^(a+1) with mantissa digits m (no
       leading or trailing zeros) and a the power of ten of m's first digit, an integer of any size. With
       F = the digit count of |a| in 10 digits followed by the digits of |a|, the exponent field is '1' + F when a >= 0
       and '0' + 9-complement(F) when a < 0. A positive number is '2' + field + m; a negative one is
       '0' + 9-complement(field + m) + ':' (':' sorts after every digit, so of two complemented mantissas where one is a
       prefix of the other, the longer, larger magnitude sorts first).
ExactDecimal.ToOrderKey computes the same key in C# for filter operands; the two must change together.

Cost: callers evaluate it once per stored value (see FilterWriter.NumberKey). Text of up to 4000 characters, every
realistic number, is handled here on VARCHAR (8000) values, which is about twice as fast as VARCHAR (MAX); longer text
goes to dbo.JsonNumberOrderKeyLong, the same algorithm on VARCHAR (MAX). Procedural and not inlined (INLINE = OFF): as
one inlined expression, the exact exponent arithmetic exceeds SQL Server's expression-services limit (error 8632).
*/
CREATE FUNCTION [dbo].[JsonNumberOrderKey] (@number NVARCHAR (MAX))
RETURNS VARCHAR (MAX)
WITH SCHEMABINDING, RETURNS NULL ON NULL INPUT, INLINE = OFF
AS
BEGIN
    IF DATALENGTH(@number) > 8000
        RETURN [dbo].[JsonNumberOrderKeyLong](@number);
    -- Not number-shaped (-?D(.D)?([eE][+-]?D)? with D one or more digits): NULL, never an error.
    -- Characters are checked on the NVARCHAR input under a binary collation first: the CAST below would map fullwidth
    -- and other non-ASCII digits (and superscripts) to ASCII ones under a linguistic collation.
    IF PATINDEX(N'%[^-+.0-9eE]%', @number COLLATE Latin1_General_100_BIN2) > 0
        RETURN NULL;
    DECLARE @text VARCHAR (8000) = CAST(@number AS VARCHAR (8000));
    DECLARE @negative BIT = CASE WHEN LEFT(@text, 1) = '-' THEN 1 ELSE 0 END;
    IF @negative = 1
        SET @text = STUFF(@text, 1, 1, '');

    -- Split into the significand I.F and the exponent X.
    DECLARE @at BIGINT = PATINDEX('%[eE]%', @text);
    DECLARE @significand VARCHAR (8000) = CASE WHEN @at > 0 THEN LEFT(@text, @at - 1) ELSE @text END;
    DECLARE @exponent VARCHAR (8000) = CASE WHEN @at > 0 THEN STUFF(@text, 1, @at, '') ELSE '0' END;
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
    DECLARE @digits VARCHAR (8000) = REPLACE(@significand, '.', '');
    DECLARE @first BIGINT = PATINDEX('%[^0]%', @digits);
    IF @first = 0
        RETURN '1';
    DECLARE @mantissa VARCHAR (8000) =
        SUBSTRING(@digits, @first, LEN(@digits) - PATINDEX('%[^0]%', REVERSE(@digits)) - @first + 2);

    -- a = (+/-)X + shift, exactly. The shift is bounded by the text's length; X is not.
    DECLARE @shift BIGINT = @integerLength - @first;
    DECLARE @aNegative BIT, @aDigits VARCHAR (8000);
    IF LEN(@exponent) <= 18
    BEGIN
        DECLARE @a BIGINT = CAST(@exponent AS BIGINT) * (1 - 2 * @exponentNegative) + @shift;
        SELECT @aNegative = CASE WHEN @a < 0 THEN 1 ELSE 0 END, @aDigits = CAST(ABS(@a) AS VARCHAR (20));
    END
    ELSE
    BEGIN
        -- |X| >= 10^18 exceeds any shift, so a has X's sign; add the shift to |X| in 18-digit chunks, right to left.
        DECLARE @carry BIGINT = @shift * (1 - 2 * @exponentNegative);
        DECLARE @rest VARCHAR (8000) = @exponent, @result VARCHAR (8000) = '', @chunk BIGINT;
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

    DECLARE @field VARCHAR (8000) = RIGHT('0000000000' + CAST(LEN(@aDigits) AS VARCHAR (19)), 10) + @aDigits;
    SET @field = CASE WHEN @aNegative = 0 THEN '1' + @field ELSE '0' + TRANSLATE(@field, '0123456789', '9876543210') END;
    RETURN CASE
        WHEN @negative = 0 THEN '2' + @field + @mantissa
        ELSE '0' + TRANSLATE(@field + @mantissa, '0123456789', '9876543210') + ':'
    END;
END